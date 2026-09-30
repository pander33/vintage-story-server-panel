using Panel.Agent.Content;

namespace Panel.Agent.Tests;

/// <summary>
/// Переименование каталога при обмене деревьев переживает короткую блокировку файла внутри:
/// на Windows её оставляют антивирус и индексатор сразу после записи.
/// </summary>
public sealed class GameFilesTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "vs-files-" + Guid.NewGuid().ToString("n"));

	public GameFilesTests() => Directory.CreateDirectory(directory);

	public void Dispose()
	{
		if (Directory.Exists(directory))
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public async Task Каталог_переименовывается_после_снятия_блокировки()
	{
		var source = Path.Combine(directory, "data");
		var destination = Path.Combine(directory, "data.pre-restore");
		Directory.CreateDirectory(source);
		var held = new FileStream(Path.Combine(source, "serverconfig.json"), FileMode.Create, FileAccess.Write, FileShare.None);

		// Блокировка снимается, пока идут повторы: на Windows первая попытка получает отказ.
		var release = Task.Run(async () =>
		{
			await Task.Delay(200);
			await held.DisposeAsync();
		});

		await GameFiles.MoveDirectoryAsync(source, destination, CancellationToken.None);
		await release;

		Assert.False(Directory.Exists(source));
		Assert.True(File.Exists(Path.Combine(destination, "serverconfig.json")));
	}

	[Fact]
	public async Task Занятое_назначение_отказ_сразу()
	{
		var source = Path.Combine(directory, "data");
		var destination = Path.Combine(directory, "taken");
		Directory.CreateDirectory(source);
		Directory.CreateDirectory(destination);

		await Assert.ThrowsAsync<IOException>(() => GameFiles.MoveDirectoryAsync(source, destination, CancellationToken.None));
		Assert.True(Directory.Exists(source));
	}
}
