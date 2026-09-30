using System.Text;

namespace Panel.SharedKernel;

/// <summary>
/// Журнал «одна запись — одна строка JSON» с удержанием по количеству и по дням.
/// </summary>
/// <remarks>
/// Два правила уплотнения: по размеру (16 МБ) и по времени (раз в час). Формат заморожен
/// (ADR-04), поэтому строка пишется компактно и без экранирования кириллицы, как
/// <c>json.dumps(..., ensure_ascii=False, separators=(",", ":"))</c>.
///
/// Блокировка на процесс, а не на файл: второй процесс её бы обошёл, но журналы панели пишет
/// только панель.
/// </remarks>
public sealed class JsonLinesFile
{
	private const long compactAboveBytes = 16 * 1024 * 1024;

	private const int tailBlockBytes = 64 * 1024;

	private static readonly byte[] utf8Bom = [0xEF, 0xBB, 0xBF];

	private static readonly TimeSpan compactInterval = TimeSpan.FromHours(1);

	private readonly SemaphoreSlim @lock = new(1, 1);
	private readonly string path;
	private readonly int keep;
	private readonly int? retentionDays;
	private readonly TimeProvider time;
	private DateTimeOffset lastCompact = DateTimeOffset.MinValue;

	public JsonLinesFile(string path, int keep = 5000, int? retentionDays = null, TimeProvider? time = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		this.path = path;
		this.keep = keep;
		this.retentionDays = retentionDays;
		this.time = time ?? TimeProvider.System;
	}

	public string Path => path;

	public async Task<Result<Done>> AppendAsync(string jsonLine, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(jsonLine);

		await @lock.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			var directory = System.IO.Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			await File.AppendAllTextAsync(path, jsonLine + "\n", new UTF8Encoding(false), ct).ConfigureAwait(false);
			Restrict(path);

			if (ShouldCompact())
			{
				await CompactAsync(ct).ConfigureAwait(false);
			}

			return Done.Ok;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Не удалось записать {path}: {exception.Message}");
		}
		finally
		{
			@lock.Release();
		}
	}

	/// <summary>
	/// Последние строки журнала, как их разбил бы <see cref="File.ReadAllLines(string)"/>.
	/// </summary>
	/// <remarks>
	/// Читается с конца, блоками, пока не наберётся на одну строку больше нужного: лента событий
	/// спрашивает хвост каждые три секунды на каждую открытую вкладку, а файл до уплотнения —
	/// тысячи строк. Первая строка прочитанного куска может быть обрезана, поэтому отбрасывается,
	/// если кусок начинается не с начала файла. Перевод строки в UTF-8 — всегда отдельный байт, так
	/// что граница блока не рвёт символ ни в одной из оставшихся строк.
	/// </remarks>
	public async Task<IReadOnlyList<string>> TailAsync(int count, CancellationToken ct)
	{
		if (count <= 0)
		{
			return [];
		}

		try
		{
			var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, useAsync: true);
			await using (stream.ConfigureAwait(false))
			{
				var end = stream.Length;
				var start = end;
				var newlines = 0;
				var chunks = new List<byte[]>();

				while (start > 0 && newlines <= count)
				{
					var size = (int) Math.Min(tailBlockBytes, start);
					start -= size;
					var block = new byte[size];
					stream.Seek(start, SeekOrigin.Begin);
					await stream.ReadExactlyAsync(block, ct).ConfigureAwait(false);
					newlines += block.AsSpan().Count((byte) '\n');
					chunks.Add(block);
				}

				chunks.Reverse();
				var data = chunks.SelectMany(chunk => chunk).ToArray();
				var text = start == 0 && data.AsSpan().StartsWith(utf8Bom)
					? Encoding.UTF8.GetString(data, utf8Bom.Length, data.Length - utf8Bom.Length)
					: Encoding.UTF8.GetString(data);

				var lines = new List<string>();
				using var reader = new StringReader(text);
				while (reader.ReadLine() is { } line)
				{
					lines.Add(line);
				}

				if (start > 0 && lines.Count > 0)
				{
					lines.RemoveAt(0);
				}

				return count >= lines.Count ? lines : lines.GetRange(lines.Count - count, count);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}

	private bool ShouldCompact()
	{
		var now = time.GetUtcNow();
		if (new FileInfo(path).Length > compactAboveBytes)
		{
			return true;
		}

		return retentionDays is not null && now - lastCompact >= compactInterval;
	}

	private async Task CompactAsync(CancellationToken ct)
	{
		var now = time.GetUtcNow();
		lastCompact = now;

		var cutoff = retentionDays is { } days
			? now.AddDays(-days).ToUnixTimeSeconds()
			: long.MinValue;

		var retained = new List<string>();
		foreach (var line in await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false))
		{
			// Строка без читаемой отметки времени отбрасывается: иначе журнал не уплотнился бы.
			if (Timestamp(line) is { } timestamp && timestamp >= cutoff)
			{
				retained.Add(line);
			}
		}

		if (retained.Count > keep)
		{
			retained.RemoveRange(0, retained.Count - keep);
		}

		var content = retained.Count == 0 ? string.Empty : string.Join('\n', retained) + "\n";
		await AtomicFile.WriteAsync(path, content, ct).ConfigureAwait(false);
		Restrict(path);
	}

	private static long? Timestamp(string line)
	{
		try
		{
			var node = System.Text.Json.Nodes.JsonNode.Parse(line);
			if (node is not System.Text.Json.Nodes.JsonObject item)
			{
				return null;
			}

			// «ts» у журналов событий, «started_at» у истории операций; уплотнитель общий.
			return (long?) item["ts"] ?? (long?) item["started_at"];
		}
		catch (System.Text.Json.JsonException)
		{
			return null;
		}
	}

	private static void Restrict(string path)
	{
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}
	}
}
