using Panel.SharedKernel;

namespace Panel.Observability.Tests;

public sealed class AtomicFileTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "vs-atomic-" + Guid.NewGuid().ToString("N"));

	[Fact]
	public async Task Параллельные_записи_сохраняют_полный_файл_и_не_делят_временное_имя()
	{
		var path = Path.Combine(directory, "state.json");
		var documents = Enumerable.Range(0, 12).Select(index => new string((char) ('a' + index), 100_000)).ToArray();
		var results = await Task.WhenAll(documents.Select(document => AtomicFile.WriteAsync(path, document, default)));
		Assert.All(results, result => Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Failure?.Message));
		Assert.Contains(await File.ReadAllTextAsync(path), documents);
		Assert.Single(Directory.GetFiles(directory));
	}

	[Fact]
	public async Task Отмена_не_оставляет_временный_файл()
	{
		var path = Path.Combine(directory, "state.json");
		await AtomicFile.WriteAsync(path, "before", default);
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicFile.WriteAsync(path, "after", cancellation.Token));
		Assert.Equal("before", await File.ReadAllTextAsync(path));
		Assert.Single(Directory.GetFiles(directory));
	}

	[Fact]
	public async Task Временная_блокировка_Windows_повторяет_замену()
	{
		if (!OperatingSystem.IsWindows()) return;
		var path = Path.Combine(directory, "state.json");
		await AtomicFile.WriteAsync(path, "before", default);
		var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		try
		{
			var writing = AtomicFile.WriteAsync(path, "after", default);
			await Task.Delay(100);
			Assert.False(writing.IsCompleted);
			held.Dispose();
			Assert.True((await writing).IsSuccess);
			Assert.Equal("after", await File.ReadAllTextAsync(path));
			Assert.Single(Directory.GetFiles(directory));
		}
		finally
		{
			held.Dispose();
		}
	}

	[Fact]
	public async Task Отмена_ожидания_замены_Windows_сохраняет_исходный_файл()
	{
		if (!OperatingSystem.IsWindows()) return;
		var path = Path.Combine(directory, "state.json");
		await AtomicFile.WriteAsync(path, "before", default);
		using var cancellation = new CancellationTokenSource();
		using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			var writing = AtomicFile.WriteAsync(path, "after", cancellation.Token);
			await Task.Delay(50);
			cancellation.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
		}
		Assert.Equal("before", await File.ReadAllTextAsync(path));
		Assert.Single(Directory.GetFiles(directory));
	}
	public void Dispose()
	{
		if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
	}
}
