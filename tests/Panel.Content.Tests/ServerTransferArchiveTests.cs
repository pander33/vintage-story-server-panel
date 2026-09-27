using Panel.Content.Domain;

namespace Panel.Content.Tests;

public sealed class ServerTransferArchiveTests
{
	private static readonly ArchiveEntry config = ArchiveEntry.CreateFile("data/serverconfig.json");

	[Fact]
	public void Старая_копия_и_пакет_с_манифестом_проходят_проверку()
	{
		Assert.True(ServerTransferArchive.Judge([ArchiveEntry.CreateDirectory("data/"), config, ArchiveEntry.CreateFile("data/Saves/world.vcdbs")]).IsSuccess);
		Assert.True(ServerTransferArchive.Judge([ArchiveEntry.CreateFile(ServerTransferArchive.Manifest), config]).IsSuccess);
	}

	[Theory]
	[InlineData("data/Mods/A.zip", "data/Mods/a.zip")]
	[InlineData("data/Mods/mod.zip", "data/Mods/MOD.ZIP")]
	[InlineData("data/Saves/a", "data/Saves/a/b")]
	public void Коллизия_или_файл_родитель_отвергаются(string first, string second)
	{
		Assert.False(ServerTransferArchive.Judge([config, ArchiveEntry.CreateFile(first), ArchiveEntry.CreateFile(second)]).IsSuccess);
	}

	[Theory]
	[InlineData("data/Mods/CON.zip")]
	[InlineData("data/Mods/LPT1")]
	[InlineData("data/Mods/mod?.zip")]
	[InlineData("data/Mods/a. ")]
	[InlineData("data/Mods/a:b")]
	[InlineData("data/../data/Saves/world.vcdbs")]
	[InlineData("data//Mods/mod.zip")]
	[InlineData("data/./Mods/mod.zip")]
	[InlineData("data/Mods/mod\u0001.zip")]
	[InlineData("data\\Mods\\a.zip")]
	[InlineData("other/file")]
	public void Недопустимое_имя_отвергается(string name)
	{
		Assert.False(ServerTransferArchive.Judge([config, ArchiveEntry.CreateFile(name)]).IsSuccess);
	}

	[Fact]
	public void Ссылки_запрещены_даже_если_они_ведут_внутрь_пакета() =>
		Assert.False(ServerTransferArchive.Judge([config, ArchiveEntry.CreateSymbolicLink("data/Mods/link", "other")]).IsSuccess);

	[Fact]
	public void Обязательная_конфигурация_не_может_отсутствовать() =>
		Assert.False(ServerTransferArchive.Judge([ArchiveEntry.CreateFile("data/Saves/world.vcdbs")]).IsSuccess);

	[Fact]
	public void Имя_обязательной_конфигурации_должно_совпадать_по_регистру() =>
		Assert.False(ServerTransferArchive.Judge([ArchiveEntry.CreateFile("data/SERVERCONFIG.json")]).IsSuccess);

	[Fact]
	public void Длинный_путь_отвергается() =>
		Assert.False(ServerTransferArchive.Judge([config, ArchiveEntry.CreateFile("data/Mods/" + new string('x', 241))]).IsSuccess);

	[Fact]
	public void Ненормализованное_имя_отвергается() =>
		Assert.False(ServerTransferArchive.Judge([config, ArchiveEntry.CreateFile("data/Mods/cafe\u0301.zip")]).IsSuccess);

	[Fact]
	public void Размер_пакета_ограничен_без_переполнения()
	{
		Assert.False(ServerTransferArchive.CountBytes(0, ServerTransferArchive.MaximumFileBytes + 1).IsSuccess);
		Assert.False(ServerTransferArchive.CountBytes(ServerTransferArchive.MaximumUnpackedBytes, 1).IsSuccess);
		Assert.False(ServerTransferArchive.CountBytes(0, -1).IsSuccess);
		Assert.Equal(42, ServerTransferArchive.CountBytes(40, 2).Value);
	}
}
