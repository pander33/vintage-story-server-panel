using System.IO.Compression;
using Panel.Agent.Content;
using Panel.Content.Domain;

namespace Panel.Agent.Tests;

/// <summary>
/// Пакет сервера для Windows — ZIP: раскладка, распаковка и те же запреты, что для tar.
/// </summary>
public sealed class PackageArchiveTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "vs-package-" + Guid.NewGuid().ToString("n"));

	public PackageArchiveTests() => Directory.CreateDirectory(directory);

	public void Dispose()
	{
		if (Directory.Exists(directory))
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Theory]
	[InlineData("game-1.zip", ".zip")]
	[InlineData("game-1.ZIP", ".zip")]
	[InlineData("game-1.tar.gz", ".tar.gz")]
	[InlineData("game-1.tgz", null)]
	[InlineData("game-1.exe", null)]
	public void Формат_узнаётся_по_расширению(string name, string? expected)
	{
		Assert.Equal(expected, PackageArchive.ExtensionOf(name));
	}

	[Fact]
	public async Task Пакет_без_каталога_верхнего_уровня_проходит_проверку_раскладки()
	{
		var archive = Zip("game.zip", ("VintagestoryServer.dll", "dll"), ("Lib/a.dll", "a"), ("assets/", null));

		var entries = await PackageArchive.ListAsync(archive, CancellationToken.None);
		var layout = ServerPackage.Judge(entries.Value);

		Assert.True(layout.IsSuccess);
		Assert.Equal(string.Empty, layout.Value.Root);
		Assert.Equal(3, layout.Value.Files);
	}

	/// <summary>Упаковщики на Windows пишут обратные косые черты, но сервер всё равно находится на втором уровне.</summary>
	[Fact]
	public async Task Обратная_косая_в_имени_сводится_к_прямой()
	{
		var archive = Zip("game.zip", (@"vintagestory\VintagestoryServer.dll", "dll"));

		var entries = await PackageArchive.ListAsync(archive, CancellationToken.None);
		var layout = ServerPackage.Judge(entries.Value);

		Assert.True(layout.IsSuccess);
		Assert.Equal("vintagestory", layout.Value.Root);
	}

	[Fact]
	public async Task Распаковка_кладёт_файлы_на_место()
	{
		var archive = Zip("game.zip", ("VintagestoryServer.dll", "dll"), ("Lib/a.dll", "содержимое"));
		var target = Path.Combine(directory, "out");

		var extracted = await PackageArchive.ExtractAsync(archive, target, CancellationToken.None);

		Assert.True(extracted.IsSuccess);
		Assert.Equal("содержимое", File.ReadAllText(Path.Combine(target, "Lib", "a.dll")));
	}

	[Theory]
	[InlineData("../evil.dll")]
	[InlineData("a/../../evil.dll")]
	[InlineData("C:/Windows/evil.dll")]
	[InlineData(@"..\evil.dll")]
	public async Task Запись_наружу_отвергает_весь_архив_до_первой_записи(string name)
	{
		var archive = Zip("game.zip", ("VintagestoryServer.dll", "dll"), (name, "x"));
		var target = Path.Combine(directory, "out");

		var extracted = await PackageArchive.ExtractAsync(archive, target, CancellationToken.None);

		Assert.False(extracted.IsSuccess);
		Assert.False(File.Exists(Path.Combine(target, "VintagestoryServer.dll")));
		Assert.False(File.Exists(Path.Combine(directory, "evil.dll")));
	}

	[Fact]
	public async Task Битый_архив_не_читается()
	{
		var archive = Path.Combine(directory, "broken.zip");
		File.WriteAllText(archive, "это не zip");

		var entries = await PackageArchive.ListAsync(archive, CancellationToken.None);

		Assert.False(entries.IsSuccess);
	}

	private string Zip(string name, params (string Entry, string? Content)[] entries)
	{
		var path = Path.Combine(directory, name);
		using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
		foreach (var (entry, content) in entries)
		{
			var created = zip.CreateEntry(entry);
			if (content is not null)
			{
				using var writer = new StreamWriter(created.Open());
				writer.Write(content);
			}
		}

		return path;
	}
}
