using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Panel.Content.Ports;
using Panel.Contracts;
using Panel.Contracts.ServerCommands;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.Maintenance.Domain;
using Panel.Observability.Domain;
using Panel.Runtime.Domain;
using Panel.Runtime.Ports;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Плановое обслуживание и наблюдатель готовности для каждого сервера реестра.
/// </summary>
/// <remarks>
/// <para>
/// Такт выполняется раз в <c>PANEL_AUTOMATION_INTERVAL</c> секунд для каждого сервера:
/// проверка готовности (с автоперезапуском зависшего сервера, если он разрешён), затем
/// задания, срок которых наступил.
/// </para>
/// <para>
/// Цепочку вердиктов (сколько проверок подряд сервер не отвечает) ведёт только этот такт.
/// <c>/api/status</c> отдаёт последний вердикт, а не вычисляет новый, иначе каждое обновление
/// страницы считалось бы ещё одной неудачей и сервер «зависал» бы раньше порога.
/// </para>
/// </remarks>
public sealed partial class AutomationService : BackgroundService, IReadinessWaiter
{
	/// <summary>Задание, пропустившее срок больше чем на шесть часов, не выполняется.</summary>
	private static readonly TimeSpan missedTooLong = TimeSpan.FromHours(6);

	private static readonly TimeSpan readinessPoll = TimeSpan.FromSeconds(5);

	private readonly ConcurrentDictionary<InstanceId, Lazy<Task<Workspace>>> workspaces = new();
	private readonly IRegistryStore registry;
	private readonly IServerRuntime runtime;
	private readonly ITelemetryReader telemetry;
	private readonly ReadinessPolicy policy;
	private readonly ReadinessLog readiness;
	private readonly ServerControlHandler control;
	private readonly IPlayerCommands players;
	private readonly IBackupStore backups;
	private readonly EventBus events;
	private readonly MaintenanceCoordinator coordinator;
	private readonly PanelOptions options;
	private readonly TimeProvider clock;
	private readonly ILogger<AutomationService> log;
	private readonly TimeZoneInfo zone = TimeZoneInfo.Local;
	private readonly CancellationTokenSource stopping = new();

	public AutomationService(
		IRegistryStore registry,
		IServerRuntime runtime,
		ITelemetryReader telemetry,
		ReadinessPolicy policy,
		ReadinessLog readiness,
		ServerControlHandler control,
		IPlayerCommands players,
		IBackupStore backups,
		EventBus events,
		MaintenanceCoordinator coordinator,
		PanelOptions options,
		TimeProvider clock,
		ILogger<AutomationService> log)
	{
		this.registry = registry;
		this.runtime = runtime;
		this.telemetry = telemetry;
		this.policy = policy;
		this.readiness = readiness;
		this.control = control;
		this.players = players;
		this.backups = backups;
		this.events = events;
		this.coordinator = coordinator;
		this.options = options;
		this.clock = clock;
		this.log = log;
	}

	/// <summary>Занят ли сервер обслуживанием: изменяющие маршруты отвечают 409.</summary>
	public bool IsBusy(InstanceId instance) => coordinator.IsBusy(instance);

	// ----- такт ----------------------------------------------------------------------

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, stopping.Token);
		using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.AutomationIntervalSeconds));

		while (await timer.WaitForNextTickAsync(linked.Token).ConfigureAwait(false))
		{
			var snapshot = await registry.LoadAsync(linked.Token).ConfigureAwait(false);
			if (snapshot is not RegistrySnapshot.Available available)
			{
				continue;
			}

			await Task.WhenAll(available.Servers.Select(server => SafelyAsync(server.Id, linked.Token))).ConfigureAwait(false);
		}
	}

	public override async Task StopAsync(CancellationToken cancellationToken)
	{
		await stopping.CancelAsync().ConfigureAwait(false);
		await base.StopAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task SafelyAsync(InstanceId instance, CancellationToken ct)
	{
		try
		{
			await TickAsync(instance, ct).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			TickFailed(log, instance.Value, exception);
		}
	}

	private async Task TickAsync(InstanceId instance, CancellationToken ct)
	{
		await CheckWatchdogAsync(instance, ct).ConfigureAwait(false);

		var workspace = await WorkspaceAsync(instance).ConfigureAwait(false);
		var now = clock.GetUtcNow();
		MaintenanceJob? due = null;
		var skipped = new List<MaintenanceJob>();

		lock (workspace.Sync)
		{
			if (coordinator.IsLocked || workspace.Running is not null)
			{
				return;
			}

			for (var index = 0; index < workspace.Jobs.Count && due is null; index++)
			{
				var job = workspace.Jobs[index];
				if (!job.Enabled || job.NextRun is not { } next || next > now.ToUnixTimeSeconds())
				{
					continue;
				}

				workspace.Jobs[index] = job.Advance(now, zone);

				// Панель была выключена дольше шести часов, и начинать обслуживание в случайное время нельзя:
				// прогон записывается как пропущенный.
				if (now.ToUnixTimeSeconds() - next > (long) missedTooLong.TotalSeconds)
				{
					skipped.Add(job);
					continue;
				}

				due = workspace.Jobs[index];
			}

			if (due is null && skipped.Count == 0)
			{
				return;
			}
		}

		foreach (var job in skipped)
		{
			var run = new MaintenanceRun(Guid.NewGuid().ToString("N"), job.Id, job.Name, "schedule", now.ToUnixTimeSeconds());
			run.SkipMissed(now.ToUnixTimeSeconds());
			await RecordRunAsync(instance, workspace, run, withTrigger: false, ct).ConfigureAwait(false);
		}

		if (due is not null)
		{
			Launch(instance, workspace, due, "schedule");
		}

		await workspace.SaveAsync(ct).ConfigureAwait(false);
	}

	// ----- наблюдатель ---------------------------------------------------------------

	/// <summary>
	/// Проверка готовности; при вердикте «завис» и разрешённом автоперезапуске сервер
	/// перезапускается в пределах бюджета.
	/// </summary>
	public async Task<JsonObject> CheckWatchdogAsync(InstanceId instance, CancellationToken ct)
	{
		var workspace = await WorkspaceAsync(instance).ConfigureAwait(false);

		await workspace.WatchdogGate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			var now = clock.GetUtcNow();
			var previousState = TelemetryNumbers.Text(workspace.Health["state"]);

			var status = await runtime.StatusAsync(instance, ct).ConfigureAwait(false);
			if (!status.IsSuccess)
			{
				workspace.Health = new JsonObject
				{
					["state"] = "unknown",
					["detail"] = status.Failure!.Message,
					["checked_at"] = now.ToUnixTimeSeconds(),
					["failures"] = 0,
				};
				workspace.Chain = null;
			}
			else
			{
				var telemetry = await this.telemetry.ReadAsync(instance, ct).ConfigureAwait(false);
				var verdict = policy.Evaluate(status.Value, telemetry, workspace.Watchdog, workspace.Chain, now);
				workspace.Chain = verdict;
				workspace.Health = HealthJson(verdict);

				if (verdict.AuthorizesAutomaticRecovery && workspace.Watchdog.AutoRestart)
				{
					await RecoverAsync(instance, workspace, now, ct).ConfigureAwait(false);
				}
			}

			// Перезапуски старше часа забываются при каждой проверке, а не только при попытке
			// восстановления; так же выглядит restart_history.
			workspace.Restarts = [.. RecoveryBudget.Recent(workspace.Restarts, now)];
			readiness.Record(instance, workspace.Chain, RecoveryBlocked(workspace.Health));
			await workspace.SaveAsync(ct).ConfigureAwait(false);

			var state = TelemetryNumbers.Text(workspace.Health["state"]);
			if (state != previousState && state is "ready" or "hung" or "stopped")
			{
				await events.EmitAsync(
						instance,
						"watchdog." + state,
						state == "hung" ? EventSeverity.Critical : state == "stopped" ? EventSeverity.Warning : EventSeverity.Info,
						"Watchdog: " + TelemetryNumbers.Text(workspace.Health["detail"]),
						source: "watchdog",
						ct: ct)
					.ConfigureAwait(false);
			}

			return (JsonObject) workspace.Health.DeepClone();
		}
		finally
		{
			workspace.WatchdogGate.Release();
		}
	}

	/// <summary>Последний вердикт наблюдателя; его отдаёт <c>/api/status</c>.</summary>
	public async Task<(ReadinessVerdict? Verdict, JsonObject Health)> CurrentHealthAsync(InstanceId instance)
	{
		var workspace = await WorkspaceAsync(instance).ConfigureAwait(false);
		return (workspace.Chain, (JsonObject) workspace.Health.DeepClone());
	}

	public async Task<WatchdogSettings> WatchdogAsync(InstanceId instance) =>
		(await WorkspaceAsync(instance).ConfigureAwait(false)).Watchdog;

	private async Task RecoverAsync(InstanceId instance, Workspace workspace, DateTimeOffset now, CancellationToken ct)
	{
		workspace.Restarts = [.. RecoveryBudget.Recent(workspace.Restarts, now)];
		var blocker = RecoveryBudget.Blocker(workspace.Restarts, workspace.Watchdog, now);
		if (blocker is not null)
		{
			workspace.Health["recovery_blocked"] = blocker;
			return;
		}

		using var lease = coordinator.TryAcquire(instance, LeasePurpose.WatchdogRecovery);
		if (lease is null)
		{
			workspace.Health["recovery_blocked"] = "Выполняется другая общая операция";
			return;
		}

		var restarted = await control.ServiceAsync(instance, ServiceAction.Restart, ct).ConfigureAwait(false);
		if (!restarted.IsSuccess)
		{
			workspace.Health["recovery_blocked"] = "Ошибка перезапуска: " + restarted.Failure!.Message;
			return;
		}

		workspace.Restarts.Add(now);

		// После перезапуска цепочка начинается заново: у нового процесса свой счёт.
		workspace.Chain = null;
		workspace.Health = new JsonObject
		{
			["state"] = "recovering",
			["detail"] = "Watchdog перезапустил зависший сервер",
			["checked_at"] = now.ToUnixTimeSeconds(),
			["failures"] = 0,
		};

		var run = new MaintenanceRun(Guid.NewGuid().ToString("N"), null, "Watchdog recovery", "watchdog", now.ToUnixTimeSeconds());
		run.Recovered(Now());
		await RecordRunAsync(instance, workspace, run, withTrigger: true, ct).ConfigureAwait(false);
	}

	public async Task<Result<JsonObject>> SetWatchdogAsync(InstanceId instance, JsonObject values, CancellationToken ct)
	{
		var settings = WatchdogSettingsJson.FromJson(values);
		if (!settings.IsSuccess)
		{
			return Result.Fail<JsonObject>(settings.Failure!);
		}

		var workspace = await WorkspaceAsync(instance).ConfigureAwait(false);
		workspace.Watchdog = settings.Value;
		readiness.Configure(instance, settings.Value);
		await workspace.SaveAsync(ct).ConfigureAwait(false);
		return WatchdogSettingsJson.ToJson(settings.Value);
	}

	// ----- задания -------------------------------------------------------------------

	public async Task<JsonObject> SnapshotAsync(InstanceId instance, CancellationToken ct)
	{
		var health = await CheckWatchdogAsync(instance, ct).ConfigureAwait(false);
		var workspace = await WorkspaceAsync(instance).ConfigureAwait(false);
		var runs = await workspace.RecentRunsAsync(30, ct).ConfigureAwait(false);

		lock (workspace.Sync)
		{
			return new JsonObject
			{
				["jobs"] = new JsonArray([.. workspace.Jobs
					.OrderBy(job => job.NextRun ?? long.MaxValue)
					.Select(job => (JsonNode?)job.ToJson())]),
				["runs"] = new JsonArray([.. runs.Select(run => (JsonNode?) run)]),
				["running"] = workspace.Running?.ToJson(),
				["watchdog"] = WatchdogSettingsJson.ToJson(workspace.Watchdog),
				["health"] = health,
			};
		}
	}

	public async Task<Result<JsonObject>> AddJobAsync(InstanceId instance, JobDraft draft, CancellationToken ct)
	{
		var job = MaintenanceJob.Create(draft, null, clock.GetUtcNow(), zone);
		if (!job.IsSuccess)
		{
			return Result.Fail<JsonObject>(job.Failure!);
		}

		var workspace = await WorkspaceAsync(instance).ConfigureAwait(false);
		lock (workspace.Sync)
		{
			workspace.Jobs.Add(job.Value);
		}

		await workspace.SaveAsync(ct).ConfigureAwait(false);
		return job.Value.ToJson();
	}

	/// <summary>Изменение и переключение: присланное накладывается на задание, как <c>current | raw</c>.</summary>
	public async Task<Result<JsonObject>> UpdateJobAsync(
		InstanceId instance,
		string id,
		Func<JobDraft, JobDraft> change,
		CancellationToken ct)
	{
		var workspace = await WorkspaceAsync(instance).ConfigureAwait(false);
		Result<MaintenanceJob> updated;

		lock (workspace.Sync)
		{
			var index = workspace.Jobs.FindIndex(job => job.Id == id);
			if (index < 0)
			{
				return Failure.NotFound("Задание не найдено");
			}

			var current = workspace.Jobs[index];
			updated = MaintenanceJob.Create(change(current.ToDraft()), id, clock.GetUtcNow(), zone, previous: current);
			if (updated.IsSuccess)
			{
				workspace.Jobs[index] = updated.Value;
			}
		}

		if (!updated.IsSuccess)
		{
			return Result.Fail<JsonObject>(updated.Failure!);
		}

		await workspace.SaveAsync(ct).ConfigureAwait(false);
		return updated.Value.ToJson();
	}

	public async Task<Result<Done>> DeleteJobAsync(InstanceId instance, string id, CancellationToken ct)
	{
		var workspace = await WorkspaceAsync(instance).ConfigureAwait(false);
		lock (workspace.Sync)
		{
			if (workspace.Jobs.RemoveAll(job => job.Id == id) == 0)
			{
				return Failure.NotFound("Задание не найдено");
			}
		}

		await workspace.SaveAsync(ct).ConfigureAwait(false);
		return Done.Ok;
	}

	/// <summary>Запуск вручную. Если панель занята, 409.</summary>
	public async Task<Result<string>> LaunchAsync(InstanceId instance, string id)
	{
		var workspace = await WorkspaceAsync(instance).ConfigureAwait(false);
		MaintenanceJob? job;
		lock (workspace.Sync)
		{
			job = workspace.Jobs.Find(item => item.Id == id);
		}

		if (job is null)
		{
			return Failure.NotFound("Задание не найдено");
		}

		var runId = Launch(instance, workspace, job, "manual");
		return runId is null ? Failure.Conflict("Уже выполняется другое обслуживание") : runId;
	}

	/// <summary>
	/// Аренда берётся здесь, синхронно с проверкой занятости, и только потом создаётся задача,
	/// поэтому два запроса не могут одновременно получить «свободно».
	/// </summary>
	private string? Launch(InstanceId instance, Workspace workspace, MaintenanceJob job, string trigger)
	{
		MaintenanceRun run;
		lock (workspace.Sync)
		{
			if (workspace.Running is not null)
			{
				return null;
			}

			var lease = coordinator.TryAcquire(instance, LeasePurpose.ScheduledMaintenance);
			if (lease is null)
			{
				return null;
			}

			run = new MaintenanceRun(Guid.NewGuid().ToString("N"), job.Id, job.Name, trigger, clock.GetUtcNow().ToUnixTimeSeconds());
			workspace.Running = run;
			_ = Task.Run(() => ExecuteRunAsync(instance, workspace, job, run, lease));
		}

		return run.Id;
	}

	/// <summary>
	/// Прогон: предупреждения игрокам, остановка, копия, запуск, ожидание готовности, либо только
	/// копия.
	/// </summary>
	private async Task ExecuteRunAsync(
		InstanceId instance,
		Workspace workspace,
		MaintenanceJob job,
		MaintenanceRun run,
		MaintenanceCoordinator.Lease lease)
	{
		var ct = stopping.Token;
		using (lease)
		{
			run.Begin();
			try
			{
				var status = await runtime.StatusAsync(instance, ct).ConfigureAwait(false);
				var active = status.IsSuccess && status.Value.IsActive;
				var warnings = active ? job.WarningMinutes : [];

				if (job.WarningMinutes.Count > 0 && !active)
				{
					run.Step(new RunStep("countdown", "skipped", "Сервер уже остановлен"));
				}

				var previous = warnings.Count > 0 ? warnings[0] : 0;
				for (var index = 0; index < warnings.Count; index++)
				{
					var minutes = warnings[index];
					if (index > 0)
					{
						await Task.Delay(TimeSpan.FromMinutes(previous - minutes), ct).ConfigureAwait(false);
					}

					var announce = PlayerCommand.Create("announce", string.Empty, $"Плановое обслуживание через {minutes} мин.");
					var sent = await players.SendAsync(instance, announce.Value, ct).ConfigureAwait(false);
					run.Step(sent.IsSuccess
						? new RunStep("announce", "ok", Minutes: minutes)
						: new RunStep("announce", "warning", sent.Failure!.Message));
					previous = minutes;
				}

				if (warnings.Count > 0 && previous > 0)
				{
					await Task.Delay(TimeSpan.FromMinutes(previous), ct).ConfigureAwait(false);
				}

				if (job.Restart)
				{
					Require(await control.ServiceAsync(instance, ServiceAction.Stop, ct).ConfigureAwait(false));
					run.Step(new RunStep("stop", "ok"));

					try
					{
						if (job.Backup)
						{
							var backup = Require(await backups.CreateAsync(instance, offline: true, ct).ConfigureAwait(false));
							run.Step(new RunStep("backup", "ok", BackupName: backup.Name));
						}
					}
					finally
					{
						// После неудачной копии сервер не должен остаться остановленным.
						Require(await control.ServiceAsync(instance, ServiceAction.Start, CancellationToken.None).ConfigureAwait(false));
					}

					run.Step(new RunStep("start", "ok"));

					if (!await WaitReadyAsync(instance, TimeSpan.FromSeconds(job.ReadinessTimeoutSeconds), ct).ConfigureAwait(false))
					{
						throw new MaintenanceFailed(Failure.Unavailable("Сервер не перешёл в состояние «Готов» за отведённое время"));
					}

					run.Step(new RunStep("wait_ready", "ok"));
				}
				else if (job.Backup)
				{
					var backup = Require(await backups.CreateAsync(instance, offline: false, ct).ConfigureAwait(false));
					run.Step(new RunStep("backup", "ok", BackupName: backup.Name));
				}

				run.Succeed(Now());
			}
			catch (OperationCanceledException)
			{
				run.Cancel(Now());
			}
			catch (MaintenanceFailed failure)
			{
				run.Fail(failure.Reason, Now());
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				// Любой отказ означает неудачный прогон: задача запущена без наблюдения, и исключение иначе
				// нигде бы не записалось.
				run.Fail(Failure.Unavailable(exception.Message), Now());
			}
			finally
			{
				lock (workspace.Sync)
				{
					var index = workspace.Jobs.FindIndex(item => item.Id == job.Id);
					if (index >= 0)
					{
						workspace.Jobs[index] = workspace.Jobs[index].WithResult(run.StartedAt, run.Status);
					}

					workspace.Running = null;
				}

				await RecordRunAsync(instance, workspace, run, withTrigger: true, CancellationToken.None).ConfigureAwait(false);
				await workspace.SaveAsync(CancellationToken.None).ConfigureAwait(false);
			}
		}
	}

	/// <summary>
	/// Ожидание готовности. Сервер без мода телеметрии готовность подтвердить не может, поэтому
	/// «ограниченная проверка» завершает ожидание сразу, а не по таймауту.
	/// </summary>
	public async Task<bool> WaitReadyAsync(InstanceId instance, TimeSpan timeout, CancellationToken ct)
	{
		var deadline = clock.GetUtcNow() + timeout;
		while (clock.GetUtcNow() < deadline)
		{
			var health = await CheckWatchdogAsync(instance, ct).ConfigureAwait(false);
			switch (TelemetryNumbers.Text(health["state"]))
			{
				case "ready":
					return true;
				case "degraded":
					return false;
			}

			await Task.Delay(readinessPoll, ct).ConfigureAwait(false);
		}

		return false;
	}

	private async Task RecordRunAsync(InstanceId instance, Workspace workspace, MaintenanceRun run, bool withTrigger, CancellationToken ct)
	{
		var record = run.ToJson();
		if (!withTrigger)
		{
			record.Remove("trigger");
			record.Remove("steps");
		}

		await workspace.AppendRunAsync(record, ct).ConfigureAwait(false);

		var status = RunStatusWire.ToWire(run.Status);
		await events.EmitAsync(
				instance,
				"automation." + status,
				run.Status is RunStatus.Success or RunStatus.Skipped ? EventSeverity.Info : EventSeverity.Error,
				$"Обслуживание: {run.Name}",
				run.Detail ?? string.Empty,
				"automation",
				ct: ct)
			.ConfigureAwait(false);
	}

	private long Now() => clock.GetUtcNow().ToUnixTimeSeconds();

	private static T Require<T>(Result<T> result) =>
		result.IsSuccess ? result.Value : throw new MaintenanceFailed(result.Failure!);

	internal static JsonObject HealthJson(ReadinessVerdict verdict) => new()
	{
		["checked_at"] = verdict.CheckedAt.ToUnixTimeSeconds(),
		["instance"] = verdict.Observed.Serialize(),
		["active"] = verdict.ServiceActive,
		["state"] = verdict.State.ToWire(),
		["detail"] = verdict.Detail,
		["failures"] = verdict.ConsecutiveFailures,
	};

	private static string? RecoveryBlocked(JsonObject health) =>
		TelemetryNumbers.Text(health["recovery_blocked"]) is { Length: > 0 } blocked ? blocked : null;

	private Task<Workspace> WorkspaceAsync(InstanceId instance) =>
		workspaces.GetOrAdd(instance, id => new Lazy<Task<Workspace>>(() => OpenAsync(id))).Value;

	/// <summary>
	/// Состояние сервера из файла. Вердикт и настройки сразу видны в <c>/api/status</c>, а
	/// сохранённая цепочка продолжается после перезапуска панели.
	/// </summary>
	private async Task<Workspace> OpenAsync(InstanceId instance)
	{
		var workspace = await Workspace.OpenAsync(
				Path.Combine(options.InstanceDirectory(instance), "automation.json"),
				Path.Combine(options.InstanceDirectory(instance), "automation-runs.jsonl"),
				clock,
				zone)
			.ConfigureAwait(false);

		readiness.Configure(instance, workspace.Watchdog);
		readiness.Record(instance, workspace.Chain, RecoveryBlocked(workspace.Health));
		return workspace;
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "Такт автоматизации сервера {Instance} не удался")]
	private static partial void TickFailed(ILogger logger, string instance, Exception exception);

	/// <summary>Отказ шага, который прерывает прогон; его причина становится итогом.</summary>
	private sealed class MaintenanceFailed(Failure reason) : Exception(reason.Message)
	{
		public Failure Reason { get; } = reason;
	}

	/// <summary>
	/// Состояние автоматизации одного сервера: <c>automation.json</c> и
	/// <c>automation-runs.jsonl</c> (ADR-04).
	/// </summary>
	private sealed class Workspace
	{
		private readonly string stateFile;
		private readonly JsonLinesFile runs;
		private readonly SemaphoreSlim saving = new(1, 1);

		private Workspace(string stateFile, string runsFile, TimeProvider clock)
		{
			this.stateFile = stateFile;
			runs = new JsonLinesFile(runsFile, keep: 5000, time: clock);
		}

		public object Sync { get; } = new();

		public SemaphoreSlim WatchdogGate { get; } = new(1, 1);

		public List<MaintenanceJob> Jobs { get; private set; } = [];

		public WatchdogSettings Watchdog { get; set; } = WatchdogSettings.Default;

		public JsonObject Health { get; set; } = new()
		{
			["state"] = "unknown",
			["detail"] = "Проверка ещё не выполнялась",
			["failures"] = 0,
		};

		/// <summary>Предыдущий вердикт, от которого считаются неудачи подряд.</summary>
		public ReadinessVerdict? Chain { get; set; }

		public List<DateTimeOffset> Restarts { get; set; } = [];

		public MaintenanceRun? Running { get; set; }

		public static async Task<Workspace> OpenAsync(string stateFile, string runsFile, TimeProvider clock, TimeZoneInfo zone)
		{
			var workspace = new Workspace(stateFile, runsFile, clock);
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
		/// <c>{"version": 1, "jobs", "watchdog", "health", "restart_history"}</c>, отступ в два
		/// пробела, права 0600, не больше двадцати последних перезапусков.
		/// </summary>
		public async Task SaveAsync(CancellationToken ct)
		{
			JsonObject document;
			lock (Sync)
			{
				document = new JsonObject
				{
					["version"] = 1,
					["jobs"] = new JsonArray([.. Jobs.Select(job => (JsonNode?) job.ToJson())]),
					["watchdog"] = WatchdogSettingsJson.ToJson(Watchdog),
					["health"] = Health.DeepClone(),
					["restart_history"] = new JsonArray([.. Restarts.TakeLast(20).Select(moment => (JsonNode?) moment.ToUnixTimeSeconds())]),
				};
			}

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
			}
			finally
			{
				saving.Release();
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
				Detail = TelemetryNumbers.Text(health["detail"]),
				ConsecutiveFailures = (int) TelemetryNumbers.Number(health["failures"]),
				Observed = identity.IsSuccess ? identity.Value : ProcessIdentity.Unknown,
				ServiceActive = TelemetryNumbers.Truthy(health["active"]),
				CheckedAt = DateTimeOffset.FromUnixTimeSeconds((long) TelemetryNumbers.Number(health["checked_at"])),
			};
		}
	}
}
