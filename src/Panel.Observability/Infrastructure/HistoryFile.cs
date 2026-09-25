using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Panel.Contracts;

namespace Panel.Observability.Infrastructure;

/// <summary>
/// История в JSONL: <c>instances/&lt;id&gt;/telemetry.jsonl</c> и <c>metrics.jsonl</c>.
/// </summary>
/// <remarks>
/// <para>
/// Формат заморожен (ADR-04): одна запись — один объект JSON в строке. Файлы принадлежат
/// веб-процессу. Каталог создаётся с правами 0700.
/// </para>
/// <para>
/// Ошибка записи истории не останавливает сбор: снимок уже в памяти и будет показан, а ошибка
/// пишется в журнал панели.
/// </para>
/// </remarks>
public sealed class HistoryFile
{
	private readonly string path;
	private readonly SemaphoreSlim gate = new(1, 1);

	public HistoryFile(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		this.path = path;
	}

	public string Path => path;

	/// <summary>Все записи файла; строки, которые не разбираются, пропускаются.</summary>
	public async Task<IReadOnlyList<JsonObject>> LoadAsync(CancellationToken ct)
	{
		if (!File.Exists(path))
		{
			return [];
		}

		var result = new List<JsonObject>();
		try
		{
			foreach (var line in await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false))
			{
				if (Parse(line) is { } item)
				{
					result.Add(item);
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return result;
		}

		return result;
	}

	public async Task<bool> AppendAsync(JsonObject value, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(value);

		await gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			EnsureDirectory();
			await File.AppendAllTextAsync(path, Line(value), new UTF8Encoding(false), ct).ConfigureAwait(false);
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>Переписать файл заданными записями; используется для уплотнения истории метрик.</summary>
	public async Task<bool> RewriteAsync(IEnumerable<JsonObject> values, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(values);

		await gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			return await ReplaceAsync(values.Select(Line), ct).ConfigureAwait(false);
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>
	/// Отбросить строки старше <paramref name="cutoff"/> по полю <paramref name="timeKey"/>. Строки
	/// разбираются только до этого поля: история за сутки большая, и разбирать каждый снимок ради
	/// одной метки ни к чему. Чтение и замена идут под одной блокировкой, иначе дозапись между
	/// ними потерялась бы.
	/// </summary>
	public async Task<bool> PruneAsync(string timeKey, long cutoff, CancellationToken ct)
	{
		var pattern = new Regex("\"" + Regex.Escape(timeKey) + @"""\s*:\s*(?<value>-?\d+(?:\.\d+)?)");

		await gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			if (!File.Exists(path))
			{
				return true;
			}

			var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
			var kept = lines.Where(line =>
			{
				var match = pattern.Match(line);
				return match.Success
					&& double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var stamp)
					&& stamp >= cutoff;
			});

			return await ReplaceAsync(kept.Select(line => line + '\n'), ct).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>Запись во временный файл рядом и переименование. Вызывать под блокировкой.</summary>
	private async Task<bool> ReplaceAsync(IEnumerable<string> lines, CancellationToken ct)
	{
		var temporary = path + ".tmp";
		try
		{
			EnsureDirectory();
			await File.WriteAllTextAsync(temporary, string.Concat(lines), new UTF8Encoding(false), ct).ConfigureAwait(false);
			File.Move(temporary, path, overwrite: true);
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
		finally
		{
			if (File.Exists(temporary))
			{
				File.Delete(temporary);
			}
		}
	}

	private void EnsureDirectory()
	{
		var directory = System.IO.Path.GetDirectoryName(path);
		if (string.IsNullOrEmpty(directory) || Directory.Exists(directory))
		{
			return;
		}

		Directory.CreateDirectory(directory);
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
	}

	private static string Line(JsonObject value) => value.ToJsonString(PanelJson.Options) + "\n";

	private static JsonObject? Parse(string line)
	{
		if (string.IsNullOrWhiteSpace(line))
		{
			return null;
		}

		try
		{
			return JsonNode.Parse(line) as JsonObject;
		}
		catch (JsonException)
		{
			return null;
		}
	}
}
