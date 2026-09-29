using Panel.Cli;

namespace Panel.Cli.Tests;

/// <summary>Удаление при деинсталляции: файла или его каталога может уже не быть.</summary>
public sealed class FilesTests
{
	/// <summary><c>vs-panel uninstall</c> падал на машине без <c>/etc/sudoers.d</c>.</summary>
	[Fact]
	public void Файл_в_отсутствующем_каталоге_не_ошибка()
	{
		var missing = Path.Combine(Path.GetTempPath(), "vs-files-" + Guid.NewGuid().ToString("n"), "vs-panel");

		Files.Delete(missing);

		Assert.False(File.Exists(missing));
	}

	[Fact]
	public void Существующий_файл_удаляется()
	{
		var path = Path.GetTempFileName();

		Files.Delete(path);

		Assert.False(File.Exists(path));
	}
}
