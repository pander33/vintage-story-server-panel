using Panel.Content.Domain;
using Panel.SharedKernel;

namespace Panel.Content.Tests;

/// <summary>
/// Имена файлов: непроверенное имя мода или мира попало бы в путь на диске.
/// </summary>
public sealed class NameTests
{
	[Theory]
	[InlineData("survival_1.0.0.zip", true)]
	[InlineData("Mod Name 1.0.zip", true)]
	[InlineData("мод.zip", false)]
	[InlineData("../evil.zip", false)]
	[InlineData("/etc/passwd.zip", false)]
	[InlineData(".hidden.zip", false)]
	[InlineData("mod.tar.gz", false)]
	[InlineData("", false)]
	public void Имя_мода(string value, bool accepted) =>
		Assert.Equal(accepted, ModArchiveName.Create(value).IsSuccess);

	/// <summary>
	/// У имени мира набор символов шире, чем у мода: миры с кириллицей в имени уже есть на
	/// серверах.
	/// </summary>
	[Theory]
	[InlineData("default.vcdbs", true)]
	[InlineData("Мой мир.vcdbs", true)]
	[InlineData("..vcdbs", true)]
	[InlineData("world.VCDBS", true)]
	[InlineData("../world.vcdbs", false)]
	[InlineData("sub/world.vcdbs", false)]
	[InlineData(@"sub\world.vcdbs", false)]
	[InlineData("world.db", false)]
	[InlineData(".", false)]
	[InlineData("..", false)]
	public void Имя_мира(string value, bool accepted) =>
		Assert.Equal(accepted, WorldFileName.Create(value).IsSuccess);

	[Fact]
	public void Имя_мира_длиннее_двухсот_символов_отвергается() =>
		Assert.False(WorldFileName.Create(new string('a', 201) + ".vcdbs").IsSuccess);

	[Fact]
	public void Управляющий_символ_в_имени_мира_отвергается() =>
		Assert.False(WorldFileName.Create("world\n.vcdbs").IsSuccess);

	[Fact]
	public void Расширение_дописывается_при_создании_мира() =>
		Assert.Equal("new.vcdbs", WorldFileName.CreateAddingExtension("new").Value.Value);

	[Fact]
	public void Расширение_не_удваивается() =>
		Assert.Equal("new.vcdbs", WorldFileName.CreateAddingExtension("new.vcdbs").Value.Value);

	[Theory]
	[InlineData("vintagestory-2026-09-21_10-00-00.tar.gz", true)]
	[InlineData("before-restore-2026-09-21_10-00-00.tar.gz", true)]
	[InlineData("чужой-архив.tar.gz", false)]
	[InlineData("vintagestory-2026.tar", false)]
	[InlineData("../vintagestory-2026.tar.gz", false)]
	public void Имя_резервной_копии(string value, bool accepted) =>
		Assert.Equal(accepted, BackupName.Create(value).IsSuccess);

	/// <summary>
	/// Приставка определяет вид копии: страховочную копию политика удержания не удаляет.
	/// </summary>
	[Fact]
	public void Страховочная_копия_не_считается_обычной()
	{
		Assert.True(BackupName.Parse("vintagestory-2026-09-21_10-00-00.tar.gz").IsRoutine);
		Assert.False(BackupName.Parse("before-restore-2026-09-21_10-00-00.tar.gz").IsRoutine);
	}

	[Fact]
	public void Архивный_мир_помнит_исходное_имя() =>
		Assert.Equal("Мой мир.vcdbs", StoredWorldName.Parse("20260921-100000-Мой мир.vcdbs").OriginalName);

	/// <summary>
	/// При совпадении имён хранилище добавляет второе число. Исходное имя должно читаться и тогда,
	/// иначе интерфейс покажет служебную метку.
	/// </summary>
	[Fact]
	public void Архивный_мир_помнит_исходное_имя_при_столкновении() =>
		Assert.Equal("world.vcdbs", StoredWorldName.Parse("20260921-100000-2-world.vcdbs").OriginalName);

	[Fact]
	public void Архивный_мир_без_метки_остаётся_собой() =>
		Assert.Equal("world.vcdbs", StoredWorldName.Parse("world.vcdbs").OriginalName);

	[Theory]
	[InlineData("Основной", true)]
	[InlineData("survival only", true)]
	[InlineData("набор/1", false)]
	[InlineData("", false)]
	public void Имя_профиля_модов(string value, bool accepted) =>
		Assert.Equal(accepted, ModProfileName.Create(value).IsSuccess);

	[Fact]
	public void Имя_профиля_длиннее_сорока_восьми_символов_отвергается() =>
		Assert.False(ModProfileName.Create(new string('a', 49)).IsSuccess);

	[Theory]
	[InlineData("mod-abcdef.zip", true)]
	[InlineData("world-abcdef.vcdbs", true)]
	[InlineData("config-abcdef.json", true)]
	[InlineData("passwd", false)]
	[InlineData("../mod-abcdef.zip", false)]
	public void Имя_временного_файла(string value, bool accepted) =>
		Assert.Equal(accepted, StagedFile.Create(value).IsSuccess);

	[Theory]
	[InlineData("survival", true)]
	[InlineData("SURVIVAL", true)]
	[InlineData("mod_id.v2", true)]
	[InlineData("mod id", false)]
	[InlineData("../mod", false)]
	public void Идентификатор_мода(string value, bool accepted) =>
		Assert.Equal(accepted, ModId.Create(value).IsSuccess);

	[Fact]
	public void Идентификатор_мода_приводится_к_нижнему_регистру() =>
		Assert.Equal("survival", ModId.Parse("Survival").Value);
}
