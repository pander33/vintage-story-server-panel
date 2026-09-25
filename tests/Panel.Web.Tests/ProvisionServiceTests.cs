using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Panel.Catalogues.Infrastructure;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.Maintenance.Domain;
using Panel.Observability.Infrastructure;
using Panel.Provisioning.Domain;
using Panel.Provisioning.Ports;
using Panel.Runtime.Domain;
using Panel.Runtime.Ports;
using Panel.SharedKernel;
using Panel.Web.Application;

namespace Panel.Web.Tests;

/// <summary>
/// Создание сервера на подменённых портах: порядок шагов, готовность, отказы до и после
/// регистрации, проверка машины.
/// </summary>
public sealed class ProvisionServiceTests : IDisposable
{
	private static readonly byte[] package = "package"u8.ToArray();

	private readonly string state = Path.Combine(Path.GetTempPath(), "vs-provision-" + Guid.NewGuid().ToString("n"));
	private readonly List<string> calls = [];
	private readonly List<Uri> downloads = [];
	private readonly FakeHost host;
	private readonly FakeRegistry registry = new();
	private readonly FakeRuntime runtime;
	private readonly FakeTelemetry telemetry = new();
	private readonly PanelOptions options;
	private readonly MaintenanceCoordinator coordinator = new();
	private readonly ProvisionService service;

	public ProvisionServiceTests()
	{
		options = new PanelOptions { StateDirectory = state };
		host = new FakeHost(calls);
		runtime = new FakeRuntime(calls);

		var md5 = Convert.ToHexString(MD5.HashData(package)).ToLowerInvariant();
		var manifest = new JsonObject
		{
			["1.22.7"] = new JsonObject
			{
				["linuxserver"] = new JsonObject
				{
					["filename"] = "vs_server_linux-x64_1.22.7.tar.gz",
					["filesize"] = "200 MB",
					["md5"] = md5,
					["urls"] = new JsonObject { ["cdn"] = "https://cdn.vintagestory.at/gamefiles/stable/vs_server_linux-x64_1.22.7.tar.gz" },
					["latest"] = 1,
				},
			},
		};

		var catalogue = new VersionCatalogue(
			Path.Combine(state, "catalogue.json"),
			_ => Task.FromResult(Result.Ok((JsonObject) manifest.DeepClone())),
			TimeProvider.System);
		var downloader = new PackageDownloader(new HttpClient(new Handler(request =>
		{
			downloads.Add(request.RequestUri!);
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) };
		})));

		service = new ProvisionService(
			host,
			registry,
			catalogue,
			downloader,
			new StagingWriter(options),
			coordinator,
			runtime,
			telemetry,
			new ReadinessPolicy(),
			new EventBus(options, TimeProvider.System, new NotificationSettingsFile(Path.Combine(state, "notifications.json")), NullLogger<EventBus>.Instance),
			options,
			TimeProvider.System,
			NullLogger<ProvisionService>.Instance)
		{
			ReadinessPoll = TimeSpan.FromMilliseconds(1),
			ReadinessTimeout = TimeSpan.FromSeconds(5),
		};
	}

	public void Dispose()
	{
		if (Directory.Exists(state))
		{
			Directory.Delete(state, recursive: true);
		}
	}

	private static ServerSpec Spec(string version = "1.22.7") => ServerSpec.FromWizard(new ServerAnswers
	{
		Id = "creative",
		Name = "Творческий",
		Version = version,
		Port = 42421,
		ServerName = "Творческий сервер",
		MaxPlayers = 16,
		Pvp = true,
		Language = "ru",
		WorldName = "Новый мир",
		Playstyle = "creativebuilding",
	}).Value;

	private async Task<JsonObject> RunAsync(ServerSpec? spec = null)
	{
		var launched = await service.LaunchAsync(spec ?? Spec());
		Assert.True(launched.IsSuccess, launched.Failure?.Message);
		await service.StopAsync(CancellationToken.None);

		var snapshot = await service.SnapshotAsync(CancellationToken.None);
		Assert.False((bool) snapshot["running"]!);
		Assert.Null(snapshot["current"]);
		var record = snapshot["history"]!.AsArray()[0]!.AsObject();
		Assert.Equal(launched.Value.ToString(), (string?) record["id"]);
		return record;
	}

	private static IEnumerable<string?> Actions(JsonObject record) =>
		record["steps"]!.AsArray().Select(step => (string?) step!["action"]);

	private string[] Staged() =>
		Directory.Exists(options.StagingDirectory) ? Directory.GetFiles(options.StagingDirectory) : [];

	[Fact]
	public async Task Созданный_сервер_скачан_зарегистрирован_запущен_и_готов()
	{
		var record = await RunAsync();

		Assert.Equal("success", (string?) record["status"]);
		Assert.Equal("Сервер создан, запущен и отвечает", (string?) record["detail"]);
		Assert.Equal(["catalogue", "download", "create", "service", "start", "ready"], Actions(record));
		Assert.Equal(["instance-create", "instance-service-install", "service Start"], calls.Where(call => call != "status"));
		Assert.Equal("/srv/vs-panel/creative", (string?) record["root"]);
		Assert.Equal("cdn.vintagestory.at", Assert.Single(downloads).Host);
		Assert.Empty(Staged());

		// Демону отправлено описание с закрытым набором ключей и пакетом из staging.
		var sent = host.Specification!;
		Assert.StartsWith(StagedFile.Purpose.Game, (string?) sent["package"], StringComparison.Ordinal);
		Assert.True(InstanceSpecification.Parse(sent).IsSuccess);
	}

	[Fact]
	public async Task Сервер_без_мода_телеметрии_всё_равно_считается_созданным()
	{
		telemetry.Reading = new TelemetryReading.Unavailable("Файл телеметрии не найден");
		runtime.Uptime = TimeSpan.FromSeconds(400);

		var record = await RunAsync();

		Assert.Equal("success", (string?) record["status"]);
		Assert.Equal("Сервер создан и запущен, но готовность подтвердить не удалось: Файл телеметрии не найден", (string?) record["detail"]);
		Assert.Equal("warning", (string?) record["steps"]!.AsArray()[^1]!["status"]);
		Assert.Equal("degraded", (string?) record["health"]!["state"]);
	}

	[Fact]
	public async Task Сервер_в_цикле_перезапусков_не_ждёт_до_конца_срока()
	{
		telemetry.Reading = new TelemetryReading.Unavailable("нет");
		runtime.Crashing = true;

		var started = DateTime.UtcNow;
		var record = await RunAsync();

		Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4), "ожидание должно закончиться сразу");
		Assert.Equal("failed", (string?) record["health"]!["state"]);
		Assert.Contains("перезапускается после ошибки запуска", (string?) record["detail"], StringComparison.Ordinal);
	}

	[Fact]
	public async Task Отказ_до_регистрации_не_оставляет_ничего_в_staging()
	{
		host.CreateFailure = Failure.Unavailable("Extracted package has no server DLL");

		var record = await RunAsync();

		Assert.Equal("failed", (string?) record["status"]);
		Assert.Equal("Extracted package has no server DLL", (string?) record["detail"]);
		Assert.Equal(["catalogue", "download"], Actions(record));
		Assert.Empty(Staged());
	}

	[Fact]
	public async Task Отказ_после_регистрации_не_удаляет_сервер()
	{
		host.InstallFailure = Failure.Unavailable("Console wrapper is not installed");

		var record = await RunAsync();

		Assert.Equal("failed", (string?) record["status"]);
		Assert.Equal(["catalogue", "download", "create", "rollback"], Actions(record));
		var rollback = record["steps"]!.AsArray()[^1]!;
		Assert.Equal("skipped", (string?) rollback["status"]);
		Assert.Equal("Сервер уже зарегистрирован; удалите его вручную, если он не нужен", (string?) rollback["detail"]);
	}

	[Fact]
	public async Task Неизвестная_версия_отказывает_до_скачивания()
	{
		var record = await RunAsync(Spec("1.99.0"));

		Assert.Equal("failed", (string?) record["status"]);
		Assert.Equal("Версия 1.99.0 отсутствует в каталоге", (string?) record["detail"]);
		Assert.Empty(downloads);
		Assert.Empty(Actions(record));
	}

	[Theory]
	[InlineData("creative", 42000, "Сервер creative уже существует")]
	[InlineData("main", 42421, "Порт 42421 уже занят сервером main")]
	public async Task Конфликт_реестра_выясняется_до_скачивания(string id, int port, string message)
	{
		registry.Entries.Add(new RegistryEntryPayload { Id = id, Port = port });

		var record = await RunAsync();

		Assert.Equal(message, (string?) record["detail"]);
		Assert.Empty(downloads);
		Assert.DoesNotContain("instance-create", calls);
	}

	[Fact]
	public async Task Реестр_только_для_чтения_не_даёт_создать_сервер()
	{
		registry.Writable = false;

		var record = await RunAsync();

		Assert.Equal("Реестр серверов доступен только на чтение; выполните scripts/install.sh", (string?) record["detail"]);
	}

	[Fact]
	public async Task Занятая_панель_не_начинает_второе_создание()
	{
		using var held = coordinator.TryAcquire(InstanceId.Parse("main"), LeasePurpose.ScheduledMaintenance);

		var refused = await service.LaunchAsync(Spec());

		Assert.Equal("Уже выполняется обслуживание или другая операция", refused.Failure!.Message);
		Assert.Empty(calls);
	}

	[Fact]
	public async Task Проверка_машины_блокирует_без_dotnet_setpriv_и_записываемого_реестра()
	{
		host.Runtime = new RuntimeProbePayload();
		registry.Writable = false;

		var report = await service.PreflightAsync(null, CancellationToken.None);

		Assert.False((bool) report["ok"]!);
		var blockers = report["blockers"]!.AsArray().Select(item => (string?) item).ToList();
		Assert.Contains(blockers, text => text!.StartsWith("Не найден dotnet", StringComparison.Ordinal));
		Assert.Contains(blockers, text => text!.StartsWith("Не найден setpriv", StringComparison.Ordinal));
		Assert.Contains(blockers, text => text!.StartsWith("Реестр серверов доступен только на чтение", StringComparison.Ordinal));
		Assert.Null(report["release"]);
	}

	[Fact]
	public async Task Проверка_машины_блокирует_на_маленьком_диске()
	{
		host.Free = 1024L * 1024 * 1024;

		var report = await service.PreflightAsync(ChosenVersion.Create("1.22.7"), CancellationToken.None);

		Assert.False((bool) report["ok"]!);
		Assert.Equal("Недостаточно места: свободно 1 ГиБ, нужно около 3 ГиБ.", (string?) report["blockers"]!.AsArray().Single());
		Assert.Equal(3L * 1024 * 1024 * 1024, (long) report["required_free"]!);
	}

	[Fact]
	public async Task Проверка_здоровой_машины_проходит()
	{
		registry.Entries.Add(new RegistryEntryPayload { Id = "main", Port = 42420 });

		var report = await service.PreflightAsync(ChosenVersion.Create("1.22.7"), CancellationToken.None);

		Assert.True((bool) report["ok"]!, report.ToJsonString());
		Assert.Equal(
			["blockers", "warnings", "runtime", "instances", "used_ports", "used_ids", "release", "required_free", "free", "ok"],
			report.Select(pair => pair.Key));
		Assert.Equal("1.22.7", (string?) report["release"]!["version"]);
		Assert.Equal([42420], report["used_ports"]!.AsArray().Select(port => (int) port!));
	}

	[Fact]
	public async Task Прерванное_создание_записывается_а_не_забывается()
	{
		Directory.CreateDirectory(state);
		var running = Operation.CreateProvision(OperationId.New(), InstanceId.Parse("creative"), "1.22.7", 100);
		await File.WriteAllTextAsync(
			options.ProvisionStatePath,
			new JsonObject { ["version"] = 1, ["current"] = running.ToJson() }.ToJsonString());

		var recovered = await service.RecoverAsync();

		Assert.Equal("interrupted", (string?) recovered!["status"]);
		Assert.Equal(OperationRecovery.InterruptedProvisionDetail, (string?) recovered["detail"]);
		var snapshot = await service.SnapshotAsync(CancellationToken.None);
		Assert.Single(snapshot["history"]!.AsArray());
	}

	private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(respond(request));
	}

	private sealed class FakeHost(List<string> calls) : IProvisioningHost
	{
		public RuntimeProbePayload Runtime { get; set; } = new()
		{
			Dotnet = "/usr/bin/dotnet",
			Majors = [10],
			Setpriv = "/usr/bin/setpriv",
		};

		public long Free { get; set; } = 100L * 1024 * 1024 * 1024;

		public Failure? CreateFailure { get; set; }

		public Failure? InstallFailure { get; set; }

		public JsonObject? Specification { get; private set; }

		public Task<Result<RuntimeProbePayload>> RuntimeProbeAsync(CancellationToken ct) =>
			Task.FromResult(Result.Ok(Runtime));

		public Task<Result<DiskFreePayload>> DiskFreeAsync(CancellationToken ct) =>
			Task.FromResult(Result.Ok(new DiskFreePayload { Free = Free }));

		public Task<Result<InstanceCreatedPayload>> CreateInstanceAsync(ServerSpec spec, StagedFile package, CancellationToken ct)
		{
			calls.Add("instance-create");
			Specification = spec.ToJson(package);
			return Task.FromResult(CreateFailure is { } failure
				? Result.Fail<InstanceCreatedPayload>(failure)
				: Result.Ok(new InstanceCreatedPayload
				{
					Id = spec.Id.Value,
					Root = "/srv/vs-panel/" + spec.Id.Value,
					Unit = $"vs-{spec.Id.Value}.service",
					TelemetryMod = "vspaneltelemetry_0.4.0.zip",
				}));
		}

		public Task<Result<ServiceInstalledPayload>> InstallServiceAsync(InstanceId instance, CancellationToken ct)
		{
			calls.Add("instance-service-install");
			return Task.FromResult(InstallFailure is { } failure
				? Result.Fail<ServiceInstalledPayload>(failure)
				: Result.Ok(new ServiceInstalledPayload { Id = instance.Value }));
		}

		public Task<Result<PanelEnvironmentSetPayload>> SetPanelEnvironmentAsync(PanelSetting setting, CancellationToken ct)
		{
			calls.Add($"panel-env-set {setting.Key}={setting.Value}");
			return Task.FromResult(Result.Ok(new PanelEnvironmentSetPayload { Key = setting.Key }));
		}

		public Task<Result<PanelRestartPayload>> RestartPanelAsync(CancellationToken ct)
		{
			calls.Add("panel-restart");
			return Task.FromResult(Result.Ok(new PanelRestartPayload { Unit = "vs-panel.service" }));
		}

		public Task<Result<PanelLogsPayload>> PanelLogsAsync(int lines, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<Result<InstancesRootSetPayload>> SetInstancesRootAsync(string path, CancellationToken ct) =>
			throw new NotSupportedException();
	}

	private sealed class FakeRegistry : IRegistryAdministration
	{
		public List<RegistryEntryPayload> Entries { get; } = [];

		public bool Writable { get; set; } = true;

		public Task<Result<RegistryListingPayload>> ListAsync(CancellationToken ct) =>
			Task.FromResult(Result.Ok(new RegistryListingPayload { Instances = [.. Entries], Writable = Writable }));

		public Task<Result<InstanceRenamedPayload>> RenameAsync(InstanceId instance, InstanceName name, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<Result<InstanceForgottenPayload>> ForgetAsync(InstanceId instance, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<Result<InstanceDeletedPayload>> DeleteAsync(InstanceId instance, CancellationToken ct) =>
			throw new NotSupportedException();
	}

	private sealed class FakeTelemetry : ITelemetryReader
	{
		public TelemetryReading Reading { get; set; } = new TelemetryReading.Fresh(new TelemetrySnapshot());

		public Task<TelemetryReading> ReadAsync(InstanceId instance, CancellationToken ct) => Task.FromResult(Reading);
	}

	private sealed class FakeRuntime(List<string> calls) : IServerRuntime
	{
		public TimeSpan Uptime { get; set; } = TimeSpan.FromSeconds(30);

		public bool Crashing { get; set; }

		public string Name => "fake";

		public Task<Result<ServiceSnapshot>> StatusAsync(InstanceId instance, CancellationToken ct)
		{
			calls.Add("status");
			return Task.FromResult(Result.Ok(new ServiceSnapshot
			{
				IsActive = !Crashing,
				State = Crashing ? RuntimeState.Starting : RuntimeState.Running,
				SubState = Crashing ? "auto-restart" : "running",
				Identity = ProcessIdentity.Unknown,
				Uptime = Uptime,
			}));
		}

		public Task<Result<Done>> ServiceAsync(InstanceId instance, ServiceAction action, TimeSpan timeout, CancellationToken ct)
		{
			calls.Add("service " + action);
			return Task.FromResult<Result<Done>>(Done.Ok);
		}

		public Task<Result<IReadOnlyList<LogEntry>>> LogsAsync(InstanceId instance, int count, CancellationToken ct) => throw new NotSupportedException();

		public Task<Result<LogPage>> LogsFromAsync(InstanceId instance, LogCursor cursor, CancellationToken ct) => throw new NotSupportedException();

		public Task<Result<ConsoleEcho>> SendAsync(InstanceId instance, ConsoleDispatch dispatch, CancellationToken ct) => throw new NotSupportedException();

		public Task<Result<ConsoleCapability>> CapabilitiesAsync(InstanceId instance, CancellationToken ct) => throw new NotSupportedException();
	}
}
