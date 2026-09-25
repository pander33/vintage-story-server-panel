using Panel.Content.Domain;

namespace Panel.Content.Tests;

/// <summary>Раскладка пакета сервера.</summary>
public sealed class ServerPackageTests
{
	private static PackageEntry File(string name, long size = 10) => new(name, ArchiveEntryKind.File, size);

	private static PackageEntry Folder(string name) => new(name, ArchiveEntryKind.Directory, 0);

	[Fact]
	public void Сервер_в_корне_архива()
	{
		var layout = ServerPackage.Judge([File("VintagestoryServer.dll", 100), File("Lib/a.dll", 5)]).Value;

		Assert.Equal(string.Empty, layout.Root);
		Assert.Equal(2, layout.Files);
		Assert.Equal(105, layout.UnpackedSize);
	}

	[Fact]
	public void Снимок_программы_с_каталогом_в_начале_путей()
	{
		var layout = ServerPackage.Judge([Folder("vintagestory"), File("vintagestory/VintagestoryServer.dll")]).Value;

		Assert.Equal("vintagestory", layout.Root);
	}

	[Fact]
	public void Точка_в_пути_выпадает_как_у_pathlib()
	{
		Assert.Equal("game", ServerPackage.Judge([File("./game/VintagestoryServer.dll")]).Value.Root);
		Assert.Equal("game", ServerPackage.Judge([File("game//VintagestoryServer.dll")]).Value.Root);
	}

	[Theory]
	[InlineData("../VintagestoryServer.dll")]
	[InlineData("/etc/VintagestoryServer.dll")]
	[InlineData(".")]
	[InlineData("./")]
	[InlineData("a/../../x")]
	public void Небезопасный_путь_отвергает_пакет(string name)
	{
		var judged = ServerPackage.Judge([File("VintagestoryServer.dll"), File(name)]);

		Assert.Equal("В пакете игры есть небезопасный путь", judged.Failure!.Message);
	}

	[Theory]
	[InlineData(ArchiveEntryKind.SymbolicLink)]
	[InlineData(ArchiveEntryKind.HardLink)]
	[InlineData(ArchiveEntryKind.Other)]
	public void Ссылки_и_устройства_запрещены_целиком(ArchiveEntryKind kind)
	{
		var judged = ServerPackage.Judge([File("VintagestoryServer.dll"), new PackageEntry("lib", kind, 0)]);

		Assert.False(judged.IsSuccess);
	}

	[Fact]
	public void Нужен_ровно_один_сервер_не_глубже_второго_уровня()
	{
		const string message = "В корне пакета должен быть ровно один VintagestoryServer.dll";

		Assert.Equal(message, ServerPackage.Judge([File("a/b/VintagestoryServer.dll")]).Failure!.Message);
		Assert.Equal(message, ServerPackage.Judge([File("VintagestoryServer.dll"), File("x/VintagestoryServer.dll")]).Failure!.Message);
		Assert.Equal(message, ServerPackage.Judge([Folder("VintagestoryServer.dll")]).Failure!.Message);
		Assert.Equal(message, ServerPackage.Judge([File("VintagestoryServer.dll", 13L * 1024 * 1024 * 1024)]).Failure!.Message);
	}

	[Fact]
	public void Пустой_пакет_отвергается()
	{
		Assert.Equal("Пакет игры пуст или слишком велик", ServerPackage.Judge([]).Failure!.Message);
	}
}
