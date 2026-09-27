using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Panel.Catalogues.Infrastructure;
using Panel.Content.Domain;
using Panel.Content.Ports;
using Panel.Contracts.Agent;
using Panel.Contracts.Content;
using Panel.Contracts.ServerCommands;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.Maintenance.Domain;
using Panel.Maintenance.Ports;
using Panel.Observability.Infrastructure;
using Panel.Runtime.Domain;
using Panel.Runtime.Ports;
using Panel.SharedKernel;
using Panel.Web;
using Panel.Web.Application;

namespace Panel.Web.Tests;

/// <summary>
/// Обновления на подменённых портах: порядок вызовов демона, откат при отказе, занятость,
/// пакет модов. Без демона, сервера и сети.
/// </summary>
public sealed class UpdateServiceTests : IDisposable
{
	private static readonly InstanceId main = InstanceId.Parse("main");

	private readonly string state = Path.Combine(Path.GetTempPath(), "vs-updates-" + Guid.NewGuid().ToString("n"));
	private readonly List<string> calls = [];
	private readonly FakeRuntime runtime;
	private readonly FakePackages packages;
	private readonly FakeMods mods;
	private readonly FakeReadiness readiness = new();
	private readonly Dictionary<string, Func<HttpResponseMessage>> web = [];
	private readonly PanelOptions options;
	private readonly StagingWriter staging;
	private readonly MaintenanceCoordinator coordinator = new();
	private readonly UpdateService service;

	public UpdateServiceTests()
	{
		options = new PanelOptions { StateDirectory = state, GameVersion = "1.22.7" };
		staging = new StagingWriter(options);
		runtime = new FakeRuntime(calls);
		packages = new FakePackages(calls);
		mods = new FakeMods(calls);

		service = new UpdateService(
			new GameVersions(new NoRegistry(), options),
			runtime,
			new FakePlayers(calls),
			new FakeBackups(calls),
			mods,
			packages,
			new ModDbClient(new HttpClient(new Handler(request =>
				web.TryGetValue(request.RequestUri!.Host + request.RequestUri.AbsolutePath, out var respond)
					? respond()
					: new HttpResponseMessage(HttpStatusCode.NotFound)))),
			staging,
			coordinator,
			readiness,
			new EventBus(options, TimeProvider.System, new NotificationSettingsFile(Path.Combine(state, "notifications.json")), NullLogger<EventBus>.Instance),
			options,
			TimeProvider.System,
			NullLogger<UpdateService>.Instance);
	}

	public void Dispose()
	{
		if (Directory.Exists(state))
		{
			Directory.Delete(state, recursive: true);
		}
	}

	[Fact]
	public async Task Обновление_игры_с_копией_готовностью_и_версией()
	{
		runtime.Active = true;
		var package = await StagePackageAsync();

		var launched = await service.LaunchGameAsync(main, package, "1.22.8");
		await service.StopAsync(default);

		Assert.True(launched.IsSuccess);
		Assert.Equal(
			["game-package-inspect", "status", "player-action", "service Stop", "backup-create-offline",
			 "game-update-apply", "service Start", "panel-game-version 1.22.8"],
			calls);
		var record = (await HistoryAsync())[0];
		Assert.Equal("success", record["status"]!.GetValue<string>());
		Assert.Equal("Пакет сервера установлен и проверен", record["detail"]!.GetValue<string>());
		Assert.Equal(launched.Value, record["id"]!.GetValue<string>());
		Assert.Equal("server-program-old.tar.gz", record["program_snapshot"]!.GetValue<string>());
		Assert.Equal("1.22.7", record["previous_version"]!.GetValue<string>());
		Assert.Equal(["validate", "backup", "install", "version"], Actions(record));
		Assert.False(File.Exists(staging.PathOf(package)));
	}

	[Fact]
	public async Task Неготовый_сервер_после_обновления_откатывает_программу_и_данные()
	{
		runtime.Active = true;
		readiness.Ready = false;

		await service.LaunchGameAsync(main, await StagePackageAsync(), "1.22.8");
		await service.StopAsync(default);

		Assert.Equal(
			["game-package-inspect", "status", "player-action", "service Stop", "backup-create-offline",
			 "game-update-apply", "service Start", "status", "service Stop", "game-update-rollback server-program-old.tar.gz",
			 "backup-restore vintagestory-2026-09-22_10-00-00.tar.gz", "panel-game-version 1.22.7", "service Start"],
			calls);
		var record = (await HistoryAsync())[0];
		Assert.Equal("failed", record["status"]!.GetValue<string>());
		Assert.Equal("Сервер не подтвердил готовность после обновления", record["detail"]!.GetValue<string>());
		Assert.Equal("rollback", Actions(record)[^1]);
		var steps = record["steps"]!.AsArray();
		Assert.Equal("ok", steps[steps.Count - 1]!["status"]!.GetValue<string>());
	}

	[Fact]
	public async Task Негодный_пакет_не_останавливает_сервер()
	{
		runtime.Active = true;
		packages.InspectFailure = Failure.Invalid("В корне пакета должен быть ровно один VintagestoryServer.dll");

		await service.LaunchGameAsync(main, await StagePackageAsync(), "1.22.8");
		await service.StopAsync(default);

		Assert.Equal(["game-package-inspect"], calls);
		Assert.Equal("В корне пакета должен быть ровно один VintagestoryServer.dll", (await HistoryAsync())[0]["detail"]!.GetValue<string>());
	}

	[Fact]
	public async Task Откат_без_связанной_копии_данных_не_начинается()
	{
		await service.LaunchRollbackAsync(main, "server-program-old.tar.gz");
		await service.StopAsync(default);

		Assert.Empty(calls);
		var record = (await HistoryAsync())[0];
		Assert.Equal("preflight", record["type"]!.GetValue<string>());
		Assert.Equal("Для этого снимка нет связанной offline-копии данных; автоматический откат запрещён", record["detail"]!.GetValue<string>());
	}

	[Fact]
	public async Task Откат_возвращает_программу_данные_и_прежнюю_версию()
	{
		Directory.CreateDirectory(Path.Combine(state, "instances", "main"));
		await File.WriteAllTextAsync(
			Path.Combine(state, "instances", "main", "update-runs.jsonl"),
			"""{"id":"u1","type":"game","status":"success","program_snapshot":"server-program-old.tar.gz","data_snapshot":"vintagestory-old.tar.gz","previous_version":"1.22.6"}""" + "\n");

		await service.LaunchRollbackAsync(main, "server-program-old.tar.gz");
		await service.StopAsync(default);

		Assert.Equal(
			["status", "backup-inspect vintagestory-old.tar.gz", "game-update-rollback server-program-old.tar.gz",
			 "backup-restore vintagestory-old.tar.gz", "panel-game-version 1.22.6"],
			calls);
		var record = (await HistoryAsync())[0];
		Assert.Equal("success", record["status"]!.GetValue<string>());
		Assert.True(record["panel_restart_required"]!.GetValue<bool>());
	}

	[Fact]
	public async Task Занятая_панель_отказывает_и_оставляет_пакет_маршруту()
	{
		using var held = coordinator.TryAcquire(main, LeasePurpose.ScheduledMaintenance);

		var launched = await service.LaunchGameAsync(main, await StagePackageAsync(), "1.22.8");

		Assert.Equal("Уже выполняется обновление или обслуживание", launched.Failure!.Message);
		Assert.Empty(calls);
	}

	[Fact]
	public async Task Пакет_модов_скачивает_проверяет_копирует_и_ставит()
	{
		mods.Installed = [Mod("example", "1.0.0")];
		ServeRelease("example", 7, ["1.22.7"], Zip("""{"modid":"Example","version":"2.0.0"}"""));

		var launched = await service.LaunchModsAsync(main, [new ModUpdateItem("example", 7)], 900);
		await service.StopAsync(default);

		Assert.True(launched.IsSuccess);
		Assert.Equal(["mods", "status", "backup-create-offline", "mod-install"], calls);
		var record = (await HistoryAsync())[0];
		Assert.Equal("Обновлено модов: 1", record["detail"]!.GetValue<string>());
		Assert.Equal(["download", "backup", "install"], Actions(record));
		Assert.Empty(Directory.EnumerateFiles(options.StagingDirectory));
	}

	[Fact]
	public async Task Пакет_модов_заменяет_только_включённый_мод()
	{
		mods.Installed = [Mod("other", "1.0.0")];
		ServeRelease("example", 7, ["1.22.7"], Zip("""{"modid":"example","version":"2.0.0"}"""));

		await service.LaunchModsAsync(main, [new ModUpdateItem("example", 7)], 900);
		await service.StopAsync(default);

		Assert.Equal(["mods"], calls);
		Assert.Equal("Обновление должно заменять уникальный уже включённый мод", (await HistoryAsync())[0]["detail"]!.GetValue<string>());
	}

	[Fact]
	public async Task Пакет_модов_не_берёт_релиз_другой_ветки()
	{
		mods.Installed = [Mod("example", "1.0.0")];
		ServeRelease("example", 7, ["1.21.7"], Zip("""{"modid":"example","version":"2.0.0"}"""));

		await service.LaunchModsAsync(main, [new ModUpdateItem("example", 7)], 900);
		await service.StopAsync(default);

		Assert.Equal("Пакетная операция допускает только совместимые стабильные релизы", (await HistoryAsync())[0]["detail"]!.GetValue<string>());
	}

	[Fact]
	public async Task Пакет_модов_с_отсутствующей_зависимостью_не_начинается()
	{
		mods.Installed = [Mod("example", "1.0.0")];
		ServeRelease("example", 7, ["1.22.7"], Zip("""{"modid":"example","version":"2.0.0","dependencies":{"missinglib":"1.0.0"}}"""));

		await service.LaunchModsAsync(main, [new ModUpdateItem("example", 7)], 900);
		await service.StopAsync(default);

		Assert.Equal(["mods"], calls);
		Assert.Equal("План содержит отсутствующие, устаревшие или непроверенные зависимости", (await HistoryAsync())[0]["detail"]!.GetValue<string>());
	}

	[Fact]
	public async Task План_блокирует_точно_установленный_несовместимый_релиз()
	{
		mods.Installed = [Mod("example", "2.0.0") with { DisplayName = "Example" }];
		web["mods.vintagestory.at/api/mod/example"] = () => Json(new JsonObject
		{
			["mod"] = new JsonObject
			{
				["name"] = "Example",
				["releases"] = new JsonArray(new JsonObject
				{
					["releaseid"] = 1,
					["modversion"] = "2.0.0",
					["tags"] = new JsonArray("v1.22.7"),
				}),
			},
		});

		var plan = (await service.GamePlanAsync(main, "1.23.0", default)).Value;

		Assert.False(plan["allowed"]!.GetValue<bool>());
		Assert.True(plan["branch_change"]!.GetValue<bool>());
		Assert.Equal("example", plan["blockers"]![0]!["modid"]!.GetValue<string>());
		Assert.Equal("1.22.7", plan["current_version"]!.GetValue<string>());
	}

	[Fact]
	public async Task План_без_ModDB_предупреждает_а_не_блокирует()
	{
		mods.Installed = [Mod("example", "2.0.0"), Mod("disabled", "1.0.0") with { Enabled = false }];

		var plan = (await service.GamePlanAsync(main, "1.22.8", default)).Value;

		Assert.True(plan["allowed"]!.GetValue<bool>());
		var warning = Assert.Single(plan["warnings"]!.AsArray())!;
		Assert.Equal("unverified", warning["status"]!.GetValue<string>());
		Assert.StartsWith("Проверка недоступна: ", warning["detail"]!.GetValue<string>());
	}

	// ----- вспомогательное ------------------------------------------------------------

	private async Task<StagedFile> StagePackageAsync()
	{
		var staged = StagingWriter.Reserve(StagedFile.Purpose.Game, ".tar.gz");
		using var source = new MemoryStream([1, 2, 3]);
		return (await staging.WriteAsync(source, staged, 1, default)).Value;
	}

	private async Task<IReadOnlyList<JsonObject>> HistoryAsync() =>
		(await service.SnapshotAsync(main, default))["history"]!.AsArray().Select(item => item!.AsObject()).ToList();

	private static string[] Actions(JsonObject record) =>
		[.. record["steps"]!.AsArray().Select(step => step!["action"]!.GetValue<string>())];

	private static ModEntryPayload Mod(string id, string version) => new()
	{
		Name = id + ".zip",
		Enabled = true,
		Size = 1,
		Modified = 1,
		ModId = id,
		Version = version,
	};

	private void ServeRelease(string mod, long release, string[] tags, byte[] archive)
	{
		web[$"mods.vintagestory.at/api/mod/{mod}"] = () => Json(new JsonObject
		{
			["mod"] = new JsonObject
			{
				["name"] = mod,
				["releases"] = new JsonArray(new JsonObject
				{
					["releaseid"] = release,
					["modversion"] = "2.0.0",
					["tags"] = new JsonArray([.. tags.Select(tag => (JsonNode) tag)]),
					["mainfile"] = "https://moddbcdn.vintagestory.at/mod.zip",
					["filename"] = "mod.zip",
				}),
			},
		});
		web["moddbcdn.vintagestory.at/mod.zip"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
	}

	private static byte[] Zip(string modinfo)
	{
		using var buffer = new MemoryStream();
		using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
		{
			using var writer = new StreamWriter(zip.CreateEntry("modinfo.json").Open(), new UTF8Encoding(true));
			writer.Write(modinfo);
		}

		return buffer.ToArray();
	}

	private static HttpResponseMessage Json(JsonObject body) =>
		new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

	private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(respond(request));
	}

	private sealed class NoRegistry : IRegistryStore
	{
		public Task<RegistrySnapshot> LoadAsync(CancellationToken ct) =>
			Task.FromResult<RegistrySnapshot>(new RegistrySnapshot.Unavailable("нет"));
	}

	private sealed class FakeReadiness : IReadinessWaiter
	{
		public bool Ready { get; set; } = true;

		public Task<bool> WaitReadyAsync(InstanceId instance, TimeSpan timeout, CancellationToken ct) => Task.FromResult(Ready);
	}

	private sealed class FakeRuntime(List<string> calls) : IServerRuntime
	{
		public bool Active { get; set; }

		public string Name => "fake";

		public Task<Result<ServiceSnapshot>> StatusAsync(InstanceId instance, CancellationToken ct)
		{
			calls.Add("status");
			return Task.FromResult(Result.Ok(new ServiceSnapshot
			{
				IsActive = Active,
				State = Active ? RuntimeState.Running : RuntimeState.Stopped,
				SubState = Active ? "running" : "dead",
				Identity = ProcessIdentity.Unknown,
			}));
		}

		public Task<Result<Done>> ServiceAsync(InstanceId instance, ServiceAction action, TimeSpan timeout, CancellationToken ct)
		{
			calls.Add("service " + action);
			Active = action != ServiceAction.Stop;
			return Task.FromResult<Result<Done>>(Done.Ok);
		}

		public Task<Result<IReadOnlyList<LogEntry>>> LogsAsync(InstanceId instance, int count, CancellationToken ct) => throw new NotSupportedException();

		public Task<Result<LogPage>> LogsFromAsync(InstanceId instance, LogCursor cursor, CancellationToken ct) => throw new NotSupportedException();

		public Task<Result<ConsoleEcho>> SendAsync(InstanceId instance, ConsoleDispatch dispatch, CancellationToken ct) => throw new NotSupportedException();

		public Task<Result<ConsoleCapability>> CapabilitiesAsync(InstanceId instance, CancellationToken ct) => throw new NotSupportedException();
	}

	private sealed class FakePlayers(List<string> calls) : IPlayerCommands
	{
		public Task<Result<PlayerActionPayload>> SendAsync(InstanceId instance, PlayerCommand command, CancellationToken ct)
		{
			calls.Add("player-action");
			return Task.FromResult(Result.Ok(new PlayerActionPayload { Action = command.Action }));
		}
	}

	private sealed class FakeBackups(List<string> calls) : IBackupStore
	{
		public Task<Result<WorldExportPayload>> ExportPackageAsync(InstanceId instance, StagedFile destination, CancellationToken ct) => throw new NotSupportedException();

		public Task<Result<BackupChangePayload>> CreateAsync(InstanceId instance, bool offline, CancellationToken ct)
		{
			calls.Add(offline ? "backup-create-offline" : "backup-create");
			return Task.FromResult(Result.Ok(new BackupChangePayload { Name = "vintagestory-2026-09-22_10-00-00.tar.gz" }));
		}

		public Task<Result<BackupContentsPayload>> InspectAsync(InstanceId instance, BackupName name, CancellationToken ct)
		{
			calls.Add("backup-inspect " + name.Value);
			return Task.FromResult(Result.Ok(new BackupContentsPayload { Name = name.Value }));
		}

		public Task<Result<BackupChangePayload>> RestoreAsync(InstanceId instance, BackupName name, CancellationToken ct)
		{
			calls.Add("backup-restore " + name.Value);
			return Task.FromResult(Result.Ok(new BackupChangePayload { Restored = name.Value }));
		}

		public Task<Result<BackupsPayload>> ListAsync(InstanceId instance, CancellationToken ct) => throw new NotSupportedException();

		public Task<Result<BackupChangePayload>> DeleteAsync(InstanceId instance, BackupName name, CancellationToken ct) => throw new NotSupportedException();

		public Task<Result<WorldChangePayload>> RestoreWorldAsync(InstanceId instance, BackupName backup, string world, WorldFileName target, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<Result<RetentionPayload>> ApplyRetentionAsync(InstanceId instance, RetentionPolicy policy, bool apply, CancellationToken ct) =>
			throw new NotSupportedException();
	}

	private sealed class FakeMods(List<string> calls) : IModStore
	{
		public IReadOnlyList<ModEntryPayload> Installed { get; set; } = [];

		public Task<Result<ModsPayload>> ListAsync(InstanceId instance, CancellationToken ct)
		{
			calls.Add("mods");
			return Task.FromResult(Result.Ok(new ModsPayload { Mods = Installed }));
		}

		public Task<Result<ModChangePayload>> InstallAsync(InstanceId instance, StagedFile staged, CancellationToken ct)
		{
			calls.Add("mod-install");
			return Task.FromResult(Result.Ok(new ModChangePayload { Name = "mod.zip", ModId = "example", Version = "2.0.0" }));
		}

		public Task<Result<ModChangePayload>> ToggleAsync(InstanceId instance, ModArchiveName mod, CancellationToken ct) => throw new NotSupportedException();

		public Task<Result<ModChangePayload>> DeleteAsync(InstanceId instance, ModArchiveName mod, CancellationToken ct) => throw new NotSupportedException();

		public Task<Result<ModChangePayload>> RollbackAsync(InstanceId instance, ModId mod, ModArchiveName archived, CancellationToken ct) => throw new NotSupportedException();

		public Task<Result<ModProfilePayload>> SaveProfileAsync(InstanceId instance, ModProfileName profile, CancellationToken ct) => throw new NotSupportedException();

		public Task<Result<ModProfilePayload>> ApplyProfileAsync(InstanceId instance, ModProfileName profile, CancellationToken ct) => throw new NotSupportedException();

		public Task<Result<Done>> DeleteProfileAsync(InstanceId instance, ModProfileName profile, CancellationToken ct) => throw new NotSupportedException();

		public Task<Result<ModConfigsPayload>> ListConfigsAsync(InstanceId instance, CancellationToken ct) => throw new NotSupportedException();

		public Task<Result<ModConfigPayload>> ReadConfigAsync(InstanceId instance, ModConfigName name, CancellationToken ct) => throw new NotSupportedException();

		public Task<Result<Done>> WriteConfigAsync(InstanceId instance, ModConfigName name, StagedFile staged, CancellationToken ct) => throw new NotSupportedException();
	}

	private sealed class FakePackages(List<string> calls) : IGamePackages
	{
		public Failure? InspectFailure { get; set; }

		public Task<Result<GamePackageInspectionPayload>> InspectAsync(InstanceId instance, StagedFile package, CancellationToken ct)
		{
			calls.Add("game-package-inspect");
			return Task.FromResult(InspectFailure is { } failure
				? Result.Fail<GamePackageInspectionPayload>(failure)
				: Result.Ok(new GamePackageInspectionPayload { Files = 10 }));
		}

		public Task<Result<GameUpdateAppliedPayload>> ApplyAsync(InstanceId instance, StagedFile package, CancellationToken ct)
		{
			calls.Add("game-update-apply");
			return Task.FromResult(Result.Ok(new GameUpdateAppliedPayload { Snapshot = "server-program-old.tar.gz" }));
		}

		public Task<Result<GameRollbackPayload>> RollbackAsync(InstanceId instance, SnapshotName snapshot, CancellationToken ct)
		{
			calls.Add("game-update-rollback " + snapshot.Value);
			return Task.FromResult(Result.Ok(new GameRollbackPayload { Restored = snapshot.Value }));
		}

		public Task<Result<GameVersionRecordedPayload>> RecordVersionAsync(InstanceId instance, string version, CancellationToken ct)
		{
			calls.Add("panel-game-version " + version);
			return Task.FromResult(Result.Ok(new GameVersionRecordedPayload { GameVersion = version }));
		}

		public Task<Result<ProgramSnapshotsPayload>> HistoryAsync(InstanceId instance, CancellationToken ct) =>
			Task.FromResult(Result.Ok(new ProgramSnapshotsPayload()));
	}
}
