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

	/// <summary>Последние строки журнала. Файл читается целиком: он уплотняется и большим не бывает.</summary>
	public async Task<IReadOnlyList<string>> TailAsync(int count, CancellationToken ct)
	{
		try
		{
			var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
			return count >= lines.Length ? lines : lines[^count..];
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
