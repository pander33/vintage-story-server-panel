using System.Globalization;
using System.Runtime.CompilerServices;
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

	/// <summary>
	/// Записи файла по одной; строки, которые не разбираются, пропускаются, ошибка чтения
	/// заканчивает перечисление.
	/// </summary>
	/// <remarks>
	/// Не списком: история телеметрии за сутки — десятки тысяч полных снимков, и разобранные
	/// разом они занимали при старте панели сотни мегабайт, хотя в памяти остаётся только выжимка.
	/// </remarks>
	public async IAsyncEnumerable<JsonObject> ReadAsync([EnumeratorCancellation] CancellationToken ct)
	{
		if (!File.Exists(path))
		{
			yield break;
		}

		IAsyncEnumerator<string>? lines = null;
		try
		{
			lines = File.ReadLinesAsync(path, ct).GetAsyncEnumerator(ct);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
		}

		if (lines is null)
		{
			yield break;
		}

		await using (lines.ConfigureAwait(false))
		{
			while (await NextAsync(lines).ConfigureAwait(false))
			{
				if (Parse(lines.Current) is { } item)
				{
					yield return item;
				}
			}
		}
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
			return await ReplaceAsync(
				async writer =>
				{
					foreach (var value in values)
					{
						await writer.WriteAsync(Line(value).AsMemory(), ct).ConfigureAwait(false);
					}
				},
				ct).ConfigureAwait(false);
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
	/// ними потерялась бы. Файл переписывается построчно, а не собирается в памяти целиком.
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

			return await ReplaceAsync(
				async writer =>
				{
					await foreach (var line in File.ReadLinesAsync(path, ct).ConfigureAwait(false))
					{
						var match = pattern.Match(line);
						if (match.Success
							&& double.TryParse(match.Groups["value"].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var stamp)
							&& stamp >= cutoff)
						{
							await writer.WriteAsync(line.AsMemory(), ct).ConfigureAwait(false);
							await writer.WriteAsync("\n".AsMemory(), ct).ConfigureAwait(false);
						}
					}
				},
				ct).ConfigureAwait(false);
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
	private async Task<bool> ReplaceAsync(Func<StreamWriter, Task> write, CancellationToken ct)
	{
		var temporary = path + ".tmp";
		try
		{
			EnsureDirectory();
			var writer = new StreamWriter(temporary, append: false, new UTF8Encoding(false));
			await using (writer.ConfigureAwait(false))
			{
				await write(writer).ConfigureAwait(false);
				await writer.FlushAsync(ct).ConfigureAwait(false);
			}

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

	/// <summary>Следующая строка; ошибка чтения — конец файла, как и прежде при чтении целиком.</summary>
	private static async Task<bool> NextAsync(IAsyncEnumerator<string> lines)
	{
		try
		{
			return await lines.MoveNextAsync().ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
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
