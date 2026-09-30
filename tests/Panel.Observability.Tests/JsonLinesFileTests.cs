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
}
