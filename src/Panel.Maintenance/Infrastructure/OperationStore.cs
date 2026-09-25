using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Contracts;
using Panel.Maintenance.Domain;
using Panel.SharedKernel;

namespace Panel.Maintenance.Infrastructure;

/// <summary>
/// Операции одного сервера: <c>instances/&lt;id&gt;/updates.json</c> с текущей записью и
/// <c>update-runs.jsonl</c> с историей.
/// </summary>
/// <remarks>
/// Текущая запись пишется после каждого шага, чтобы было видно, где оборвалась операция.
/// Завершённая операция сначала попадает в историю и только потом убирается из
/// <c>current</c>: при сбое между этими шагами запись окажется в обоих местах, но уже
/// завершённой, и восстановление её не тронет (<see cref="OperationRecovery.Interrupt"/>).
/// </remarks>
public sealed class OperationStore
{
	private readonly string stateFile;
	private readonly JsonLinesFile history;
	private readonly SemaphoreSlim saving = new(1, 1);

	private readonly OperationFamily family;

	/// <summary>Обновления одного сервера: <c>updates.json</c> и <c>update-runs.jsonl</c> в его каталоге.</summary>
	public OperationStore(string directory)
		: this(Path.Combine(directory, "updates.json"), Path.Combine(directory, "update-runs.jsonl"), OperationFamily.Updates)
	{
	}

	/// <summary>
	/// Произвольная пара файлов. Для создания серверов это <c>provision.json</c> и
	/// <c>provision-runs.jsonl</c> на уровне панели: сервера, в каталоге которого их хранить, ещё
	/// нет.
	/// </summary>
	public OperationStore(string stateFile, string historyFile, OperationFamily family)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(stateFile);
		ArgumentException.ThrowIfNullOrWhiteSpace(historyFile);
		this.stateFile = stateFile;
		history = new JsonLinesFile(historyFile);
		this.family = family;
	}

	/// <summary>
	/// Запись, оставшаяся от прошлого процесса, переносится в историю как прерванная. Вызывать
	/// один раз, до первой операции этого процесса над сервером, иначе своя идущая операция будет
	/// принята за чужую.
	/// </summary>
	public async Task<JsonObject?> RecoverAsync(long now, CancellationToken ct)
	{
		var current = await ReadCurrentAsync(ct).ConfigureAwait(false);
		if (current is null)
		{
			return null;
		}

		var interrupted = OperationRecovery.Interrupt(current, now, family);
		if (interrupted is not null)
		{
			await AppendAsync(interrupted, ct).ConfigureAwait(false);
		}

		await SaveCurrentAsync(null, ct).ConfigureAwait(false);
		return interrupted;
	}

	public async Task<JsonObject?> ReadCurrentAsync(CancellationToken ct)
	{
		try
		{
			var text = await File.ReadAllTextAsync(stateFile, ct).ConfigureAwait(false);
			return JsonNode.Parse(text) is JsonObject { } root && root["current"] is JsonObject current ? current : null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			// Файла нет или он не читается: текущей записи нет.
			return null;
		}
	}

	/// <summary><c>{"version": 1, "current": …}</c>, отступ в два пробела, права 0600.</summary>
	public async Task<Result<Done>> SaveCurrentAsync(Operation? operation, CancellationToken ct)
	{
		var document = new JsonObject { ["version"] = 1, ["current"] = operation?.ToJson() };

		await saving.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			var directory = Path.GetDirectoryName(stateFile)!;
			if (!Directory.Exists(directory))
			{
				Directory.CreateDirectory(directory);
				if (!OperatingSystem.IsWindows())
				{
					File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
				}
			}

			var written = await AtomicFile
				.WriteAsync(stateFile, document.ToJsonString(PanelJson.Indented) + "\n", ct)
				.ConfigureAwait(false);
			if (written.IsSuccess && !OperatingSystem.IsWindows())
			{
				File.SetUnixFileMode(stateFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			}

			return written;
		}
		finally
		{
			saving.Release();
		}
	}

	/// <summary>Завершённая операция: в историю, затем из <c>current</c>.</summary>
	public async Task ArchiveAsync(Operation operation, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(operation);
		await AppendAsync(operation.ToJson(), ct).ConfigureAwait(false);
		await SaveCurrentAsync(null, ct).ConfigureAwait(false);
	}

	/// <summary>Последние записи истории, новые первыми.</summary>
	public async Task<IReadOnlyList<JsonObject>> RecentAsync(int limit, CancellationToken ct)
	{
		var lines = await history.TailAsync(Math.Clamp(limit, 1, 1000), ct).ConfigureAwait(false);
		var result = new List<JsonObject>();
		foreach (var line in lines.Reverse())
		{
			try
			{
				if (JsonNode.Parse(line) is JsonObject item)
				{
					result.Add(item);
				}
			}
			catch (JsonException)
			{
				// Битая строка пропускается.
			}
		}

		return result;
	}

	private Task AppendAsync(JsonObject record, CancellationToken ct) =>
		history.AppendAsync(record.ToJsonString(PanelJson.Options), ct);
}
