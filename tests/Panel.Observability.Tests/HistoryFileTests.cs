using System.Text.Json.Nodes;
using Panel.Observability.Infrastructure;

namespace Panel.Observability.Tests;

/// <summary>
/// Файл истории читается и уплотняется построчно: суточная история телеметрии велика, чтобы
/// держать её в памяти целиком.
/// </summary>
public sealed class HistoryFileTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "vs-history-" + Guid.NewGuid().ToString("n"));
	private readonly string path;

	public HistoryFileTests()
	{
		path = Path.Combine(directory, "telemetry.jsonl");
	}

	public void Dispose()
	{
		if (Directory.Exists(directory))
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public async Task Нет_файла_нет_записей()
	{
		Assert.Empty(await ReadAllAsync(new HistoryFile(path)));
	}

	[Fact]
	public async Task Записи_читаются_по_порядку_без_испорченных_строк()
	{
		var file = new HistoryFile(path);
		await file.AppendAsync(new JsonObject { ["sampled_at"] = 1 }, default);
		await File.AppendAllTextAsync(path, "не json\n\n[1,2]\n");
		await file.AppendAsync(new JsonObject { ["sampled_at"] = 2 }, default);

		var read = await ReadAllAsync(file);

		Assert.Equal([1L, 2L], read.Select(item => item["sampled_at"]!.GetValue<long>()));
	}

	[Fact]
	public async Task Уплотнение_оставляет_свежие_строки_как_есть()
	{
		var file = new HistoryFile(path);
		await file.AppendAsync(new JsonObject { ["sampled_at"] = 100, ["name"] = "старая" }, default);
		await file.AppendAsync(new JsonObject { ["sampled_at"] = 200, ["name"] = "ёлка" }, default);
		await File.AppendAllTextAsync(path, "{\"без\":\"метки\"}\n");
		await file.AppendAsync(new JsonObject { ["sampled_at"] = 300.5 }, default);

		Assert.True(await file.PruneAsync("sampled_at", 200, default));

		Assert.Equal(
			"{\"sampled_at\":200,\"name\":\"ёлка\"}\n{\"sampled_at\":300.5}\n",
			await File.ReadAllTextAsync(path));
		Assert.False(File.Exists(path + ".tmp"));
	}

	[Fact]
	public async Task Перезапись_заменяет_содержимое()
	{
		var file = new HistoryFile(path);
		await file.AppendAsync(new JsonObject { ["ts"] = 1 }, default);

		Assert.True(await file.RewriteAsync([new JsonObject { ["ts"] = 2 }, new JsonObject { ["ts"] = 3 }], default));

		Assert.Equal("{\"ts\":2}\n{\"ts\":3}\n", await File.ReadAllTextAsync(path));
	}

	private static async Task<List<JsonObject>> ReadAllAsync(HistoryFile file)
	{
		var result = new List<JsonObject>();
		await foreach (var item in file.ReadAsync(default))
		{
			result.Add(item);
		}

		return result;
	}
}
