using Panel.SharedKernel;

namespace Panel.Content.Tests;

/// <summary>
/// Фразы подтверждения. Их набирает человек по подсказке фронтенда (ADR-16), и расхождение в
/// одной букве сделает кнопку неработающей.
/// </summary>
public sealed class ConfirmationTests
{
	[Fact]
	public void Удаление() =>
		Assert.Equal("УДАЛИТЬ world.vcdbs", Confirmation.ToDelete("world.vcdbs").Expected);

	[Fact]
	public void Архивация() =>
		Assert.Equal("АРХИВИРОВАТЬ world.vcdbs", Confirmation.ToArchive("world.vcdbs").Expected);

	[Fact]
	public void Восстановление() =>
		Assert.Equal("ВОССТАНОВИТЬ vintagestory-a.tar.gz", Confirmation.ToRestore("vintagestory-a.tar.gz").Expected);

	[Fact]
	public void Извлечение() =>
		Assert.Equal("ИЗВЛЕЧЬ new.vcdbs", Confirmation.ToExtract("new.vcdbs").Expected);

	/// <summary>Очистка относится к набору копий, и называть в ней нечего.</summary>
	[Fact]
	public void Очистка_копий() =>
		Assert.Equal("CLEAR OLD BACKUPS", Confirmation.Exactly("ОЧИСТИТЬ СТАРЫЕ КОПИИ", "CLEAR OLD BACKUPS").English);

	/// <summary>Человек набирает фразу на языке интерфейса, который видит.</summary>
	[Theory]
	[InlineData("УДАЛИТЬ world.vcdbs")]
	[InlineData("DELETE world.vcdbs")]
	public void Подходит_фраза_на_любом_языке_интерфейса(string typed) =>
		Assert.True(Confirmation.ToDelete("world.vcdbs").Check(typed).IsSuccess);

	/// <summary>Глагол одного языка с чужим предметом не проходит: формы сверяются целиком.</summary>
	[Fact]
	public void Формы_не_смешиваются() =>
		Assert.False(Confirmation.ToDelete("world.vcdbs").Check("DELETE other.vcdbs").IsSuccess);

	[Fact]
	public void Английские_глаголы()
	{
		Assert.Equal("ARCHIVE w", Confirmation.ToArchive("w").English);
		Assert.Equal("RESTORE w", Confirmation.ToRestore("w").English);
		Assert.Equal("EXTRACT w", Confirmation.ToExtract("w").English);
		Assert.Equal("w", Confirmation.ByNameAlone("w").English);
	}

	/// <summary>
	/// Удаление мода — единственное подтверждение без глагола.
	/// </summary>
	[Fact]
	public void Удаление_мода_без_глагола() =>
		Assert.Equal("survival_1.0.zip", Confirmation.ByNameAlone("survival_1.0.zip").Expected);

	[Fact]
	public void Регистр_имеет_значение() =>
		Assert.False(Confirmation.ToDelete("world.vcdbs").Check("удалить world.vcdbs").IsSuccess);

	[Fact]
	public void Пустая_фраза_не_подходит() =>
		Assert.False(Confirmation.ToDelete("world.vcdbs").Check(null).IsSuccess);

	[Fact]
	public void Окружающие_пробелы_не_мешают() =>
		Assert.True(Confirmation.ToExtract("new.vcdbs").Check("\tИЗВЛЕЧЬ new.vcdbs \n").IsSuccess);
}
