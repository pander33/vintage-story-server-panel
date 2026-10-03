using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Contracts;
using Panel.Contracts.ServerCommands;
using Panel.Maintenance.Domain;
using Panel.Observability.Domain;
using Panel.Runtime.Domain;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>Изменение состояния автоматизации: значение для ответа и его отмена.</summary>
internal readonly record struct WorkspaceChange<T>(T Value, Action Undo);

/// <summary>
/// Состояние автоматизации одного сервера: <c>automation.json</c> и
/// <c>automation-runs.jsonl</c>; прежнее состояние установок 2.0 продолжает читаться.
/// </summary>
internal sealed class AutomationWorkspace
{
	private readonly string stateFile;
	private readonly JsonLinesFile runs;
	private readonly SemaphoreSlim saving = new(1, 1);

	private AutomationWorkspace(string stateFile, string runsFile, TimeProvider clock)
	{
		this.stateFile = stateFile;
		runs = new JsonLinesFile(runsFile, keep: 5000, time: clock);
	}

	public Lock Sync { get; } = new();

	public SemaphoreSlim WatchdogGate { get; } = new(1, 1);

	public List<MaintenanceJob> Jobs { get; private set; } = [];

	public List<ScheduledAnnouncement> Announcements { get; private set; } = [];

	public WatchdogSettings Watchdog { get; set; } = WatchdogSettings.Default;

	public JsonObject Health { get; set; } = new()
	{
		["state"] = "unknown",
		["detail"] = PhraseJsonConverter.ToNode(Phrase.From("Проверка ещё не выполнялась")),
		["failures"] = 0,
	};

	/// <summary>Предыдущий вердикт, от которого считаются неудачи подряд.</summary>
	public ReadinessVerdict? Chain { get; set; }

	public List<DateTimeOffset> Restarts { get; set; } = [];

	public MaintenanceRun? Running { get; set; }

	public static async Task<AutomationWorkspace> OpenAsync(string stateFile, string runsFile, TimeProvider clock, TimeZoneInfo zone)
	{
		var workspace = new AutomationWorkspace(stateFile, runsFile, clock);
		if (!File.Exists(stateFile))
		{
			return workspace;
		}

		try
		{
			if (JsonNode.Parse(await File.ReadAllTextAsync(stateFile).ConfigureAwait(false)) is not JsonObject root)
			{
				return workspace;
			}

			var now = clock.GetUtcNow();
			var watchdog = WatchdogSettingsJson.FromJson(root["watchdog"] as JsonObject);
			workspace.Watchdog = watchdog.IsSuccess ? watchdog.Value : WatchdogSettings.Default;
			workspace.Jobs = [.. (root["jobs"] as JsonArray ?? [])
				.OfType<JsonObject>()
				.Select(item => MaintenanceJob.FromJson(item, now, zone))
				.Where(job => job.IsSuccess)
				.Select(job => job.Value)];

			// В файле, записанном до появления объявлений, ключа нет: список пуст.
			workspace.Announcements = [.. (root["announcements"] as JsonArray ?? [])
				.OfType<JsonObject>()
				.Select(item => ScheduledAnnouncement.FromJson(item, now))
				.Where(item => item.IsSuccess)
				.Select(item => item.Value)];

			if (root["health"] is JsonObject health)
			{
				workspace.Health = (JsonObject) health.DeepClone();
				workspace.Chain = Verdict(health);
			}

			workspace.Restarts = [.. (root["restart_history"] as JsonArray ?? [])
				.Select(item => (long)TelemetryNumbers.Number(item))
				.Where(seconds => seconds > 0)
				.Select(DateTimeOffset.FromUnixTimeSeconds)];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			// Нечитаемое состояние заменяется умолчаниями, чтобы панель запустилась.
		}

		return workspace;
	}

	/// <summary>
	/// <c>{"version": 1, "jobs", "announcements", "watchdog", "health", "restart_history"}</c>, отступ в два
	/// пробела, права 0600, не больше двадцати последних перезапусков.
	/// </summary>
	public async Task<Result<Done>> SaveAsync(CancellationToken ct)
	{
		await saving.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			return await WriteAsync(ct).ConfigureAwait(false);
		}
		finally
		{
			saving.Release();
		}
	}

	/// <summary>
	/// Изменение заданий или объявлений вместе с записью файла. Изменение и запись идут под тем
	/// же затвором, что и <see cref="SaveAsync"/>: иначе чужая запись успела бы унести в файл
	/// изменение, которое потом отменится. Если файл не записан, изменение отменяется в памяти,
	/// и вызывающий получает отказ, а не успех, который исчез бы после перезапуска.
	/// </summary>
	/// <param name="change">Выполняется под <see cref="Sync"/>; возвращает значение для ответа и
	/// отмену (<see cref="WorkspaceChange{T}"/>). Отмена тоже выполняется под <see cref="Sync"/>. Учёт
	/// расписания (<see cref="RecordScheduleAsync"/>) ждёт того же затвора, поэтому, пока идёт запись,
	/// задания и объявления меняет только это изменение, и отмена не затрёт сдвинутый срок.</param>
	/// <remarks>
	/// Запись не отменяется токеном запроса: отменённая на середине запись оставила бы
	/// неизвестным, попало ли изменение в файл.
	/// </remarks>
	public async Task<Result<T>> ChangeAsync<T>(Func<Result<WorkspaceChange<T>>> change, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(change);

		await saving.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			Result<WorkspaceChange<T>> changed;
			lock (Sync)
			{
				changed = change();
			}

			if (!changed.IsSuccess)
			{
				return changed.Failure!;
			}

			var written = await WriteAsync(CancellationToken.None).ConfigureAwait(false);
			if (!written.IsSuccess)
			{
				lock (Sync)
				{
					changed.Value.Undo();
				}

				return written.Failure!;
			}

			return changed.Value.Value;
		}
		finally
		{
			saving.Release();
		}
	}

	/// <summary>
	/// Учёт расписания — сдвиг срока задания или объявления, итог прогона — вместе с записью, под
	/// тем же затвором, что и <see cref="ChangeAsync{T}"/>. Иначе такт, сдвинувший срок изменённого
	/// задания, пока шла запись, потерял бы сдвиг при отмене, и обслуживание или объявление
	/// выполнилось бы второй раз.
	/// </summary>
	/// <param name="record">Выполняется под <see cref="Sync"/>; <c>false</c> — ничего не изменилось,
	/// и файл не пишется.</param>
	internal async Task<Result<Done>> RecordScheduleAsync(Func<bool> record, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(record);

		await saving.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			bool changed;
			lock (Sync)
			{
				changed = record();
			}

			return changed ? await WriteAsync(CancellationToken.None).ConfigureAwait(false) : Done.Ok;
		}
		finally
		{
			saving.Release();
		}
	}

	/// <summary>Запись файла; затвор <c>saving</c> уже взят вызывающим.</summary>
	private async Task<Result<Done>> WriteAsync(CancellationToken ct)
	{
		try
		{
			JsonObject document;
			lock (Sync)
			{
				document = new JsonObject
				{
					["version"] = 1,
					["jobs"] = new JsonArray([.. Jobs.Select(job => (JsonNode?) job.ToJson())]),
					["announcements"] = new JsonArray([.. Announcements.Select(item => (JsonNode?) item.ToJson())]),
					["watchdog"] = WatchdogSettingsJson.ToJson(Watchdog),
					["health"] = Health.DeepClone(),
					["restart_history"] = new JsonArray([.. Restarts.TakeLast(20).Select(moment => (JsonNode?) moment.ToUnixTimeSeconds())]),
				};
			}

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
			if (!written.IsSuccess)
			{
				return written.Failure!;
			}
			if (!OperatingSystem.IsWindows())
			{
				File.SetUnixFileMode(stateFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			}
			return Done.Ok;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Не удалось записать {stateFile}: {exception.Message}");
		}
	}

	/// <summary>Фоновый сценарий сообщает инфраструктурную ошибку своему обработчику/журналу.</summary>
	internal async Task SaveRequiredAsync(CancellationToken ct)
	{
		var written = await SaveAsync(ct).ConfigureAwait(false);
		if (!written.IsSuccess)
		{
			throw new IOException(written.Failure!.Message);
		}
	}

	public Task AppendRunAsync(JsonObject record, CancellationToken ct) =>
		runs.AppendAsync(record.ToJsonString(PanelJson.Options), ct);

	/// <summary>Последние прогоны, новые первыми.</summary>
	public async Task<IReadOnlyList<JsonObject>> RecentRunsAsync(int limit, CancellationToken ct)
	{
		var lines = await runs.TailAsync(limit, ct).ConfigureAwait(false);
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

	/// <summary>Сохранённый вердикт как звено цепочки: состояние, счёт, процесс.</summary>
	private static ReadinessVerdict Verdict(JsonObject health)
	{
		var identity = ProcessIdentity.Parse(TelemetryNumbers.Text(health["instance"]));

		return new ReadinessVerdict
		{
			State = HealthStateWire.FromWire(TelemetryNumbers.Text(health["state"])),
			Detail = PhraseJsonConverter.FromNode(health["detail"]) ?? Phrase.From(string.Empty),
			ConsecutiveFailures = (int) TelemetryNumbers.Number(health["failures"]),
			Observed = identity.IsSuccess ? identity.Value : ProcessIdentity.Unknown,
			ServiceActive = TelemetryNumbers.Truthy(health["active"]),
			CheckedAt = DateTimeOffset.FromUnixTimeSeconds((long) TelemetryNumbers.Number(health["checked_at"])),
		};
	}
}
