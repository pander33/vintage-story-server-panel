using Panel.Content.Domain;
using Panel.SharedKernel;

namespace Panel.Content.Tests;

/// <summary>
/// Попытки обойти распаковщик архива, по одной на каждое правило фильтра <c>data</c> (ADR-08).
/// </summary>
public sealed class ArchiveSafetyTests
{
	[Fact]
	public void Абсолютный_путь_отвергается() =>
		Rejected(ArchiveEntry.CreateFile("/etc/passwd"));

	[Fact]
	public void Выход_наверх_отвергается() =>
		Rejected(ArchiveEntry.CreateFile("../../etc/passwd"));

	[Fact]
	public void Выход_наверх_из_середины_пути_отвергается() =>
		Rejected(ArchiveEntry.CreateFile("data/../../etc/passwd"));

	/// <summary>
	/// Буква диска. На Linux такое имя не абсолютное, и распаковщик создал бы каталог <c>C:</c>,
	/// но копия может попасть на Windows, где это уже выход за пределы.
	/// </summary>
	[Fact]
	public void Путь_с_буквой_диска_отвергается() =>
		Rejected(ArchiveEntry.CreateFile(@"C:\Windows\System32\drivers\etc\hosts"));

	/// <summary>Обратная косая черта как разделитель: тот же выход в другом написании.</summary>
	[Fact]
	public void Выход_наверх_через_обратную_косую_отвергается() =>
		Rejected(ArchiveEntry.CreateFile(@"..\..\etc\passwd"));

	[Fact]
	public void Символическая_ссылка_наружу_отвергается() =>
		Rejected(ArchiveEntry.CreateSymbolicLink("data/link", "/etc/shadow"));

	/// <summary>
	/// Относительная ссылка разбирается от своего каталога: <c>data/link</c> с целью <c>../../etc</c>
	/// выходит наружу, хотя цель не начинается с <c>/</c>.
	/// </summary>
	[Fact]
	public void Относительная_ссылка_наружу_отвергается() =>
		Rejected(ArchiveEntry.CreateSymbolicLink("data/link", "../../etc/shadow"));

	[Fact]
	public void Жёсткая_ссылка_наружу_отвергается() =>
		Rejected(ArchiveEntry.CreateHardLink("data/link", "../../etc/shadow"));

	[Fact]
	public void Устройство_или_сокет_отвергается() =>
		Rejected(new ArchiveEntry("data/dev-null", ArchiveEntryKind.Other, string.Empty, 0b110_110_110));

	/// <summary>
	/// Бит setuid снимается, а архив не отвергается: в игровых данных он может оказаться случайно,
	/// и из-за этого нельзя лишать восстановления из копии.
	/// </summary>
	[Fact]
	public void Setuid_и_setgid_снимаются()
	{
		var judged = ArchiveSafety.Judge(ArchiveEntry.CreateFile("data/tool", 0b110_111_101_101));

		Assert.True(judged.IsSuccess);
		Assert.Equal(0b111_101_101, judged.Value.Mode);
	}

	/// <summary>
	/// Ссылка внутрь архива допустима: такие бывают в каталоге игры, и без них копия была бы
	/// неполной.
	/// </summary>
	[Fact]
	public void Ссылка_внутрь_архива_допускается()
	{
		var judged = ArchiveSafety.Judge(ArchiveEntry.CreateSymbolicLink("data/Mods/link.zip", "../Backups/mod.zip"));

		Assert.True(judged.IsSuccess);
	}

	/// <summary>
	/// Имя, начинающееся с двух точек, — обычное имя, хотя проверка по подстроке <c>..</c> его
	/// отвергла бы.
	/// </summary>
	[Fact]
	public void Имя_начинающееся_с_двух_точек_допускается()
	{
		var judged = ArchiveSafety.Judge(ArchiveEntry.CreateFile("data/..hidden"));

		Assert.True(judged.IsSuccess);
		Assert.Equal("data/..hidden", judged.Value.RelativePath);
	}

	/// <summary>Путь, который поднимается и возвращается обратно, остаётся внутри.</summary>
	[Fact]
	public void Путь_возвращающийся_внутрь_допускается() =>
		Assert.True(ArchiveSafety.Judge(ArchiveEntry.CreateFile("data/Saves/../Mods/mod.zip")).IsSuccess);

	[Fact]
	public void Запись_без_имени_отвергается() =>
		Rejected(ArchiveEntry.CreateFile("/"));

	/// <summary>
	/// Из-за одной записи отвергается весь архив: частично распакованная копия бесполезна.
	/// </summary>
	[Fact]
	public void Одна_опасная_запись_отвергает_весь_архив()
	{
		var judged = ArchiveSafety.Judge(
		[
			ArchiveEntry.CreateDirectory("data"),
			ArchiveEntry.CreateFile("data/serverconfig.json"),
			ArchiveEntry.CreateFile("../../etc/passwd"),
			ArchiveEntry.CreateFile("data/Saves/world.vcdbs"),
		]);

		Assert.False(judged.IsSuccess);
		Assert.Equal(FailureCode.Invalid, judged.Failure!.Code);
	}

	[Fact]
	public void Безупречный_архив_проходит_целиком()
	{
		var judged = ArchiveSafety.Judge(
		[
			ArchiveEntry.CreateDirectory("data"),
			ArchiveEntry.CreateFile("data/serverconfig.json"),
			ArchiveEntry.CreateFile("data/Saves/world.vcdbs"),
		]);

		Assert.True(judged.IsSuccess);
		Assert.Equal(3, judged.Value.Count);
	}

	/// <summary>Завершающий разделитель не меняет место.</summary>
	[Fact]
	public void Хвостовой_разделитель_срезается() =>
		Assert.Equal("data/Saves", ArchiveSafety.Judge(ArchiveEntry.CreateDirectory("data/Saves/")).Value.RelativePath);

	private static void Rejected(ArchiveEntry entry)
	{
		var judged = ArchiveSafety.Judge(entry);

		Assert.False(judged.IsSuccess, $"Запись {entry.Name} должна быть отвергнута");
		Assert.Equal(FailureCode.Invalid, judged.Failure!.Code);
	}
}
