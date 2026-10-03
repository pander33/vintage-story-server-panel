using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Panel.Maintenance.Domain;
using Panel.Runtime.Domain;
using Panel.Runtime.Ports;
using Panel.SharedKernel;
using Panel.Web.Application;

namespace Panel.Web.Tests;

public sealed class AutomationServiceTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "vs-automation-commands-" + Guid.NewGuid().ToString("N"));
	private static readonly InstanceId main = InstanceId.Create("main").Value;
	private readonly ReadinessLog readiness = new();
	private readonly FakeRuntime runtime = new();
	private readonly FakeTelemetry telemetry = new();
	private PanelOptions Options => new() { StateDirectory = directory };
	private string StateFile => Path.Combine(Options.InstanceDirectory(main), "automation.json");

	private AutomationService CreateService() => new(
		null!, runtime, telemetry, new ReadinessPolicy(), readiness, null!, null!, null!, null!,
		new MaintenanceCoordinator(), Options, TimeProvider.System, NullLogger<AutomationService>.Instance);

	[Fact]
	public async Task Чтение_автоматизации_не_проверяет_сервер_и_не_записывает_вердикт()
	{
		using var service = CreateService();
		var first = await service.SnapshotAsync(main, default);
		var second = await service.SnapshotAsync(main, default);
		Assert.Equal("unknown", first["health"]!["state"]!.GetValue<string>());
		Assert.Equal(first.ToJsonString(), second.ToJsonString());
		Assert.Null(readiness.Previous(main));
		Assert.Equal(0, runtime.Reads);
		Assert.Equal(0, telemetry.Reads);
		Assert.False(File.Exists(StateFile));
	}

	[Fact]
	public async Task Ручные_проверки_не_меняют_цепочку_такта_и_файл()
	{
		var store = new AutomationWorkspaceStore(Options, readiness, TimeProvider.System, TimeZoneInfo.Utc);
		var watchdog = new WatchdogService(store, runtime, telemetry, new ReadinessPolicy(), readiness,
			null!, null!, new MaintenanceCoordinator(), null!, TimeProvider.System);
		await watchdog.TickAsync(main, default);
		var previous = readiness.Previous(main)!;
		Assert.Equal(1, previous.ConsecutiveFailures);
		var bytes = await File.ReadAllBytesAsync(StateFile);

		await watchdog.CheckWatchdogAsync(main, default);
		await watchdog.CheckWatchdogAsync(main, default);
		Assert.Same(previous, readiness.Previous(main));
		Assert.Equal(bytes, await File.ReadAllBytesAsync(StateFile));
		await watchdog.TickAsync(main, default);
		Assert.Equal(2, readiness.Previous(main)!.ConsecutiveFailures);
	}

	[Fact]
	public async Task Чтение_сохранённого_вердикта_не_выполняет_автовосстановление()
	{
		var workspace = await AutomationWorkspace.OpenAsync(StateFile, Path.Combine(directory, "runs.jsonl"), TimeProvider.System, TimeZoneInfo.Utc);
		workspace.Watchdog = WatchdogSettings.Default with { AutoRestart = true };
		workspace.Health = new JsonObject
		{
			["state"] = "unresponsive",
			["failures"] = 2,
			["checked_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
			["instance"] = "boot:42:100",
			["active"] = true,
		};
		Assert.True((await workspace.SaveAsync(default)).IsSuccess);
		var bytes = await File.ReadAllBytesAsync(StateFile);
		using var service = CreateService();
		await service.SnapshotAsync(main, default);
		var second = await service.SnapshotAsync(main, default);
		Assert.Equal(2, second["health"]!["failures"]!.GetValue<int>());
		Assert.Equal(2, readiness.Previous(main)!.ConsecutiveFailures);
		Assert.Equal(0, runtime.Reads);
		Assert.Equal(bytes, await File.ReadAllBytesAsync(StateFile));
	}

	[Theory]
	[InlineData("job-add")]
	[InlineData("job-update")]
	[InlineData("job-delete")]
	[InlineData("announcement-add")]
	[InlineData("announcement-update")]
	[InlineData("announcement-delete")]
	[InlineData("watchdog")]
	public async Task Изменение_возвращает_отказ_при_невозможности_сохранить_состояние(string action)
	{
		using var service = CreateService();
		var job = await service.AddJobAsync(main, new JobDraft { Name = "backup" }, default);
		var announcement = await service.AddAnnouncementAsync(main, new AnnouncementDraft { Text = "Hello" }, default);
		Assert.True(job.IsSuccess);
		Assert.True(announcement.IsSuccess);
		var jobId = job.Value["id"]!.GetValue<string>();
		var announcementId = announcement.Value["id"]!.GetValue<string>();
		File.Delete(StateFile);
		Directory.CreateDirectory(StateFile);
		var before = await service.SnapshotAsync(main, default);

		Failure? failure = action switch
		{
			"job-add" => (await service.AddJobAsync(main, new JobDraft { Name = "new" }, default)).Failure,
			"job-update" => (await service.UpdateJobAsync(main, jobId, draft => draft with { Name = "new" }, default)).Failure,
			"job-delete" => (await service.DeleteJobAsync(main, jobId, default)).Failure,
			"announcement-add" => (await service.AddAnnouncementAsync(main, new AnnouncementDraft { Text = "New" }, default)).Failure,
			"announcement-update" => (await service.UpdateAnnouncementAsync(main, announcementId, draft => draft with { Text = "New" }, default)).Failure,
			"announcement-delete" => (await service.DeleteAnnouncementAsync(main, announcementId, default)).Failure,
			"watchdog" => (await service.SetWatchdogAsync(main, new JsonObject { ["auto_restart"] = true }, default)).Failure,
			_ => throw new ArgumentOutOfRangeException(nameof(action)),
		};
		Assert.NotNull(failure);
		Assert.Equal(FailureCode.Unavailable, failure.Code);

		// Отказ записи отменяет изменение и в памяти: иначе расписание выполняло бы то, о чём
		// клиенту сказали «не сохранено», а после перезапуска оно исчезло бы.
		var after = await service.SnapshotAsync(main, default);
		Assert.Equal(before["jobs"]!.ToJsonString(), after["jobs"]!.ToJsonString());
		Assert.Equal(before["announcements"]!.ToJsonString(), after["announcements"]!.ToJsonString());
		if (action == "watchdog")
		{
			Assert.False((await service.WatchdogAsync(main)).AutoRestart);
			Assert.False(readiness.Settings(main).AutoRestart);
		}
	}

	/// <summary>
	/// Такт расписания, сдвинувший срок, пока шла неудачная запись изменения, не теряет сдвиг при
	/// отмене: учёт расписания ждёт того же затвора и видит уже отменённое изменение.
	/// </summary>
	[Fact]
	public async Task Отмена_изменения_не_затирает_учёт_расписания()
	{
		var workspace = await AutomationWorkspace.OpenAsync(StateFile, Path.Combine(directory, "runs.jsonl"), TimeProvider.System, TimeZoneInfo.Utc);
		var job = MaintenanceJob.Create(new JobDraft { Name = "backup" }, null, DateTimeOffset.UtcNow, TimeZoneInfo.Utc).Value;
		workspace.Jobs.Add(job);
		Directory.CreateDirectory(StateFile);
		Task<Result<Done>>? recorded = null;
		MaintenanceJob? seen = null;

		var changed = await workspace.ChangeAsync<Done>(
			() =>
			{
				var current = workspace.Jobs[0];
				workspace.Jobs[0] = current with { Name = "edited" };
				recorded = workspace.RecordScheduleAsync(
					() =>
					{
						seen = workspace.Jobs[0];
						workspace.Jobs[0] = workspace.Jobs[0] with { NextRun = 42 };
						return true;
					},
					default);
				Assert.False(recorded.IsCompleted);
				return new WorkspaceChange<Done>(Done.Value, () => AutomationService.Replace(workspace.Jobs, item => item.Id == job.Id, current));
			},
			default);
		await recorded!;

		Assert.False(changed.IsSuccess);
		Assert.Equal("backup", seen!.Name);
		Assert.Equal("backup", workspace.Jobs[0].Name);
		Assert.Equal(42, workspace.Jobs[0].NextRun);
	}

	[Fact]
	public async Task Успешное_изменение_watchdog_сохраняется_и_публикуется()
	{
		using var service = CreateService();
		var result = await service.SetWatchdogAsync(main, new JsonObject { ["auto_restart"] = true }, default);
		Assert.True(result.IsSuccess);
		Assert.True(readiness.Settings(main).AutoRestart);
		var saved = JsonNode.Parse(await File.ReadAllTextAsync(StateFile))!;
		Assert.True(saved["watchdog"]!["auto_restart"]!.GetValue<bool>());
	}

	public void Dispose()
	{
		if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
	}

	private sealed class FakeRuntime : IServerRuntime
	{
		public int Reads { get; private set; }
		public string Name => "fake";
		public Task<Result<ServiceSnapshot>> StatusAsync(InstanceId instance, CancellationToken ct)
		{
			Reads++;
			return Task.FromResult(Result.Ok(new ServiceSnapshot
			{
				IsActive = true,
				State = RuntimeState.Running,
				SubState = "running",
				Uptime = TimeSpan.FromHours(1),
				Identity = new ProcessIdentity("boot", 42, 100),
			}));
		}
		public Task<Result<Done>> ServiceAsync(InstanceId instance, ServiceAction action, TimeSpan timeout, CancellationToken ct) => throw new NotSupportedException();
		public Task<Result<IReadOnlyList<LogEntry>>> LogsAsync(InstanceId instance, int count, CancellationToken ct) => throw new NotSupportedException();
		public Task<Result<LogPage>> LogsFromAsync(InstanceId instance, LogCursor cursor, CancellationToken ct) => throw new NotSupportedException();
		public Task<Result<ConsoleEcho>> SendAsync(InstanceId instance, ConsoleDispatch dispatch, CancellationToken ct) => throw new NotSupportedException();
		public Task<Result<ConsoleCapability>> CapabilitiesAsync(InstanceId instance, CancellationToken ct) => throw new NotSupportedException();
	}

	private sealed class FakeTelemetry : ITelemetryReader
	{
		public int Reads { get; private set; }
		public Task<TelemetryReading> ReadAsync(InstanceId instance, CancellationToken ct)
		{
			Reads++;
			return Task.FromResult<TelemetryReading>(new TelemetryReading.Stale(new TelemetrySnapshot(), TimeSpan.FromMinutes(5)));
		}
	}
}
