using System.Text;
using Panel.SharedKernel;

namespace Panel.Observability.Tests;

/// <summary>
/// Хвост журнала читается с конца файла и должен совпадать с последними строками
/// <see cref="File.ReadAllLines(string)"/>.
/// </summary>
public sealed class JsonLinesFileTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "vs-jsonl-" + Guid.NewGuid().ToString("n"));
	private readonly string path;

	public JsonLinesFileTests()
	{
		Directory.CreateDirectory(directory);
		path = Path.Combine(directory, "events.jsonl");
	}

	public void Dispose() => Directory.Delete(directory, recursive: true);

	[Fact]
	public async Task Нет_файла_пустой_хвост()
	{
		Assert.Empty(await new JsonLinesFile(path).TailAsync(10, default));
	}

	[Theory]
	[InlineData(1)]
	[InlineData(7)]
	[InlineData(200)]
	[InlineData(5000)]
	public async Task Хвост_совпадает_с_чтением_целиком(int count)
	{
		// Строки разной длины с кириллицей: границы блоков по 64 КБ приходятся на середину
		// строк и многобайтовых символов.
		var builder = new StringBuilder();
		for (var index = 0; index < 3000; index++)
		{
			builder.Append("{\"ts\":").Append(index).Append(",\"text\":\"").Append('ё', index % 97).Append("\"}\n");
		}

		await File.WriteAllTextAsync(path, builder.ToString());

		var all = await File.ReadAllLinesAsync(path);
		var tail = await new JsonLinesFile(path).TailAsync(count, default);

		Assert.Equal(all.TakeLast(count), tail);
	}

	[Theory]
	[InlineData("a\nb\nc")]
	[InlineData("a\r\nb\r\n\r\nc\r\n")]
	[InlineData("\n\n")]
	[InlineData("")]
	public async Task Концы_строк_как_у_ReadAllLines(string content)
	{
		await File.WriteAllTextAsync(path, content, new UTF8Encoding(true));

		Assert.Equal(await File.ReadAllLinesAsync(path), await new JsonLinesFile(path).TailAsync(10, default));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(3)]
	public async Task Уплотнение_оставляет_последние_свежие_записи_и_пропускает_битые(int keep)
	{
		const long now = 1_800_000_000;
		var rows = new[]
		{
			"broken", "[]", "{}", "{\"ts\":\"bad\"}", "{\"ts\":1e100}",
			$"{{\"ts\":{now - 86_401},\"text\":\"старое\"}}",
			$"{{\"ts\":{now - 86_400},\"text\":\"граница\"}}",
			$"{{\"ts\":null,\"started_at\":{now - 10},\"text\":\"операция\"}}",
			$"{{\"ts\":{now - 5},\"text\":\"событие\"}}",
		};
		await File.WriteAllLinesAsync(path, rows, new UTF8Encoding(false));
		var appended = $"{{\"ts\":{now},\"text\":\"новое ё\"}}";
		var file = new JsonLinesFile(path, keep: keep, retentionDays: 1, time: new FixedTime(now));

		Assert.True((await file.AppendAsync(appended, default)).IsSuccess);
		Assert.Equal(rows.TakeLast(3).Append(appended).TakeLast(keep), await File.ReadAllLinesAsync(path));
		var bytes = await File.ReadAllBytesAsync(path);
		Assert.DoesNotContain((byte) '\r', bytes);
		Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
		Assert.Single(Directory.GetFiles(directory));
	}

	[Fact]
	public void Отрицательный_предел_журнала_отклоняется() =>
		Assert.Throws<ArgumentOutOfRangeException>(() => new JsonLinesFile(path, keep: -1));

	private sealed class FixedTime(long seconds) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(seconds);
	}

}
