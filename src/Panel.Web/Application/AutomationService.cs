using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Panel.Content.Ports;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.Maintenance.Domain;
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

	private readonly IRegistryStore registry;
	private readonly AutomationRunJournal journal;
	private readonly ScheduledMaintenanceRunner runner;
	private readonly AutomationWorkspaceStore workspaces;
	private readonly WatchdogService watchdog;
	private readonly ScheduledAnnouncementService scheduledAnnouncements;
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
		Announcements announcements,
		IBackupStore backups,
		EventBus events,
		MaintenanceCoordinator coordinator,
		PanelOptions options,
		TimeProvider clock,
		ILogger<AutomationService> log)
	{
		this.registry = registry;
		journal = new AutomationRunJournal(events);
		workspaces = new AutomationWorkspaceStore(options, readiness, clock, zone);
		watchdog = new WatchdogService(workspaces, runtime, telemetry, policy, readiness, control, events, coordinator, journal, clock);
		scheduledAnnouncements = new ScheduledAnnouncementService(workspaces, telemetry, announcements, clock, log);
		runner = new ScheduledMaintenanceRunner(runtime, control, announcements, backups, this, journal, clock, log);
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
		await watchdog.TickAsync(instance, ct).ConfigureAwait(false);

		var workspace = await workspaces.GetAsync(instance).ConfigureAwait(false);
		await scheduledAnnouncements.AnnounceDueAsync(instance, workspace, ct).ConfigureAwait(false);

		var now = clock.GetUtcNow();
		MaintenanceJob? due = null;
		var skipped = new List<MaintenanceJob>();

		var written = await workspace.RecordScheduleAsync(() =>
		{
			if (coordinator.IsLocked || workspace.Running is not null)
			{
				return false;
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

			return due is not null || skipped.Count > 0;
		}, ct).ConfigureAwait(false);

		if (due is null && skipped.Count == 0)
		{
			return;
		}

		foreach (var job in skipped)
		{
			var run = new MaintenanceRun(Guid.NewGuid().ToString("N"), job.Id, job.Name, "schedule", now.ToUnixTimeSeconds());
			run.SkipMissed(now.ToUnixTimeSeconds());
			await journal.RecordAsync(instance, workspace, run, withTrigger: false, ct).ConfigureAwait(false);
		}

		if (due is not null)
		{
			Launch(instance, workspace, due, "schedule");
		}

		if (!written.IsSuccess)
		{
			throw new IOException(written.Failure!.Message);
		}
	}

	// ----- наблюдатель ---------------------------------------------------------------

	public Task<JsonObject> CheckWatchdogAsync(InstanceId instance, CancellationToken ct) =>
		watchdog.CheckWatchdogAsync(instance, ct);

	public Task<(ReadinessVerdict? Verdict, JsonObject Health)> CurrentHealthAsync(InstanceId instance) =>
		watchdog.CurrentHealthAsync(instance);

	public Task<WatchdogSettings> WatchdogAsync(InstanceId instance) =>
		watchdog.WatchdogAsync(instance);

	public Task<Result<JsonObject>> SetWatchdogAsync(InstanceId instance, JsonObject values, CancellationToken ct) =>
		watchdog.SetWatchdogAsync(instance, values, ct);

	// ----- задания -------------------------------------------------------------------

	public async Task<JsonObject> SnapshotAsync(InstanceId instance, CancellationToken ct)
	{
		var (_, health) = await CurrentHealthAsync(instance).ConfigureAwait(false);
		var workspace = await workspaces.GetAsync(instance).ConfigureAwait(false);
		var runs = await workspace.RecentRunsAsync(30, ct).ConfigureAwait(false);

		lock (workspace.Sync)
		{
			return new JsonObject
			{
				["jobs"] = new JsonArray([.. workspace.Jobs
					.OrderBy(job => job.NextRun ?? long.MaxValue)
					.Select(job => (JsonNode?)job.ToJson())]),
				["runs"] = new JsonArray([.. runs.Select(run => (JsonNode?) run)]),
				["announcements"] = new JsonArray([.. workspace.Announcements
					.Select(item => (JsonNode?) item.ToJson())]),
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

		var workspace = await workspaces.GetAsync(instance).ConfigureAwait(false);
		return await workspace.ChangeAsync<JsonObject>(
			() =>
			{
				workspace.Jobs.Add(job.Value);
				return new WorkspaceChange<JsonObject>(job.Value.ToJson(), () => workspace.Jobs.RemoveAll(item => item.Id == job.Value.Id));
			},
			ct).ConfigureAwait(false);
	}

	/// <summary>Изменение и переключение: присланное накладывается на задание, как <c>current | raw</c>.</summary>
	public async Task<Result<JsonObject>> UpdateJobAsync(
		InstanceId instance,
		string id,
		Func<JobDraft, JobDraft> change,
		CancellationToken ct)
	{
		var workspace = await workspaces.GetAsync(instance).ConfigureAwait(false);
		return await workspace.ChangeAsync<JsonObject>(
			() =>
			{
				var index = workspace.Jobs.FindIndex(job => job.Id == id);
				if (index < 0)
				{
					return Failure.NotFound("Задание не найдено");
				}

				var current = workspace.Jobs[index];
				var updated = MaintenanceJob.Create(change(current.ToDraft()), id, clock.GetUtcNow(), zone, previous: current);
				if (!updated.IsSuccess)
				{
					return updated.Failure!;
				}

				workspace.Jobs[index] = updated.Value;
				return new WorkspaceChange<JsonObject>(updated.Value.ToJson(), () => Replace(workspace.Jobs, job => job.Id == id, current));
			},
			ct).ConfigureAwait(false);
	}

	public async Task<Result<Done>> DeleteJobAsync(InstanceId instance, string id, CancellationToken ct)
	{
		var workspace = await workspaces.GetAsync(instance).ConfigureAwait(false);
		return await workspace.ChangeAsync<Done>(
			() =>
			{
				var index = workspace.Jobs.FindIndex(job => job.Id == id);
				if (index < 0)
				{
					return Failure.NotFound("Задание не найдено");
				}

				var removed = workspace.Jobs[index];
				workspace.Jobs.RemoveAt(index);
				return new WorkspaceChange<Done>(Done.Value, () => workspace.Jobs.Insert(Math.Min(index, workspace.Jobs.Count), removed));
			},
			ct).ConfigureAwait(false);
	}

	/// <summary>Отмена изменения: прежнее значение на место элемента с тем же идентификатором.</summary>
	internal static void Replace<T>(List<T> items, Predicate<T> same, T previous)
	{
		var index = items.FindIndex(same);
		if (index >= 0)
		{
			items[index] = previous;
		}
	}

	/// <summary>Запуск вручную. Если панель занята, 409.</summary>
	public async Task<Result<string>> LaunchAsync(InstanceId instance, string id)
	{
		var workspace = await workspaces.GetAsync(instance).ConfigureAwait(false);
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
	private string? Launch(InstanceId instance, AutomationWorkspace workspace, MaintenanceJob job, string trigger)
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
			_ = Task.Run(() => runner.RunAsync(instance, workspace, job, run, lease, stopping.Token));
		}

		return run.Id;
	}

	// ----- объявления по расписанию ---------------------------------------------------

	public Task<Result<JsonObject>> AddAnnouncementAsync(InstanceId instance, AnnouncementDraft draft, CancellationToken ct) =>
		scheduledAnnouncements.AddAnnouncementAsync(instance, draft, ct);

	public Task<Result<JsonObject>> UpdateAnnouncementAsync(
		InstanceId instance,
		string id,
		Func<AnnouncementDraft, AnnouncementDraft> change,
		CancellationToken ct) =>
		scheduledAnnouncements.UpdateAnnouncementAsync(instance, id, change, ct);

	public Task<Result<Done>> DeleteAnnouncementAsync(InstanceId instance, string id, CancellationToken ct) =>
		scheduledAnnouncements.DeleteAnnouncementAsync(instance, id, ct);

	public Task<bool> WaitReadyAsync(InstanceId instance, TimeSpan timeout, CancellationToken ct) =>
		watchdog.WaitReadyAsync(instance, timeout, ct);

	internal static JsonObject HealthJson(ReadinessVerdict verdict) => WatchdogService.HealthJson(verdict);


	[LoggerMessage(Level = LogLevel.Warning, Message = "Такт автоматизации сервера {Instance} не удался")]
	private static partial void TickFailed(ILogger logger, string instance, Exception exception);

}
