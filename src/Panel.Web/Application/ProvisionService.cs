using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Panel.Catalogues.Domain;
using Panel.Catalogues.Infrastructure;
using Panel.Contracts;
using Panel.Contracts.Agent;
using Panel.Fleet.Ports;
using Panel.Maintenance.Domain;
using Panel.Maintenance.Infrastructure;
using Panel.Observability.Domain;
using Panel.Provisioning.Domain;
using Panel.Provisioning.Ports;
using Panel.Runtime.Domain;
using Panel.Runtime.Ports;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Создание сервера от начала до конца.
/// </summary>
/// <remarks>
/// <para>
/// Устроено как <see cref="UpdateService"/> (INV-29): та же <see cref="Operation"/>, та же
/// аренда обслуживания, история в JSONL и правило, по которому запись, прерванная остановкой
/// панели, уходит в историю как прерванная. Разница в том, что ничего существующего не
/// заменяется, поэтому откат — удаление дерева, которое демон только что создал, и только до
/// записи в реестр.
/// </para>
/// <para>
/// Записи лежат на уровне панели (<c>provision.json</c>, <c>provision-runs.jsonl</c>): пока
/// операция идёт, сервера, в каталоге которого их хранить, ещё нет.
/// </para>
/// </remarks>
public sealed partial class ProvisionService : IHostedService
{
	private static readonly TimeSpan serviceTimeout = TimeSpan.FromSeconds(300);

	private readonly ConcurrentDictionary<Task, byte> running = new();
	private readonly object sync = new();
	private readonly IProvisioningHost host;
	private readonly IRegistryAdministration registry;
	private readonly VersionCatalogue catalogue;
	private readonly PackageDownloader downloader;
	private readonly StagingWriter staging;
	private readonly MaintenanceCoordinator coordinator;
	private readonly IServerRuntime runtime;
	private readonly ITelemetryReader telemetry;
	private readonly ReadinessPolicy policy;
	private readonly EventBus events;
	private readonly PanelOptions options;
	private readonly PanelInstance panelInstance;
	private readonly TimeProvider clock;
	private readonly ILogger<ProvisionService> log;
	private readonly OperationStore store;
	private readonly Lazy<Task<JsonObject?>> recovery;

	private bool busy;
	private JsonObject? current;

	public ProvisionService(
		IProvisioningHost host,
		IRegistryAdministration registry,
		VersionCatalogue catalogue,
		PackageDownloader downloader,
		StagingWriter staging,
		MaintenanceCoordinator coordinator,
		IServerRuntime runtime,
		ITelemetryReader telemetry,
		ReadinessPolicy policy,
		EventBus events,
		PanelOptions options,
		PanelInstance panelInstance,
		TimeProvider clock,
		ILogger<ProvisionService> log)
	{
		this.host = host;
		this.registry = registry;
		this.catalogue = catalogue;
		this.downloader = downloader;
		this.staging = staging;
		this.coordinator = coordinator;
		this.runtime = runtime;
		this.telemetry = telemetry;
		this.policy = policy;
		this.events = events;
		this.options = options;
		this.panelInstance = panelInstance;
		this.clock = clock;
		this.log = log;
		store = new OperationStore(options.ProvisionStatePath, options.ProvisionHistoryPath, OperationFamily.Provision);
		recovery = new Lazy<Task<JsonObject?>>(RecoverOnceAsync);
	}

	/// <summary>Пауза между проверками готовности; задаётся для тестов.</summary>
	public TimeSpan ReadinessPoll { get; init; } = TimeSpan.FromSeconds(5);

	/// <summary>Сколько ждать готовности созданного сервера, как <c>readiness_timeout=900</c>.</summary>
	public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromSeconds(900);

	/// <summary>Предел размера пакета, 4 ГБ.</summary>
	public long MaxPackageBytes { get; init; } = PackageDownloader.MaxPackageBytes;

	/// <summary>Независимый от лимитов миров и модов предел переносимого архива.</summary>
	public long MaxTransferBytes { get; init; } = 16L * 1024 * 1024 * 1024;

	public bool IsBusy
	{
		get
		{
			lock (sync)
			{
				return busy;
			}
		}
	}

	public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	/// <summary>Остановка ждёт идущее создание: наполовину созданное дерево хуже ожидания.</summary>
	public Task StopAsync(CancellationToken cancellationToken) =>
		Task.WhenAll(running.Keys).WaitAsync(cancellationToken);

	/// <summary>
	/// Запись, оставшаяся от прошлого процесса, переносится в историю как прерванная. Один раз на
	/// процесс и до первого запуска, иначе своя идущая операция будет принята за чужую.
	/// </summary>
	public Task<JsonObject?> RecoverAsync() => recovery.Value;

	/// <summary>Снимок для <c>/api/provision</c> и потока мастера: <c>{running, current, history}</c>.</summary>
	public async Task<JsonObject> SnapshotAsync(CancellationToken ct)
	{
		await RecoverAsync().ConfigureAwait(false);
		var history = await store.RecentAsync(20, ct).ConfigureAwait(false);
		lock (sync)
		{
			return new JsonObject
			{
				["running"] = busy,
				["current"] = current?.DeepClone(),
				["history"] = new JsonArray([.. history.Select(item => (JsonNode?) item)]),
			};
		}
	}

	/// <summary>
	/// Всё, что можно проверить до первой записи. Интерфейс мастера показывает отчёт как есть.
	/// </summary>
	public async Task<JsonObject> PreflightAsync(ChosenVersion? version, CancellationToken ct)
	{
		var blockers = new JsonArray();
		var warnings = new JsonArray();
		var report = new JsonObject { ["blockers"] = blockers, ["warnings"] = warnings };

		var probed = await host.RuntimeProbeAsync(ct).ConfigureAwait(false);
		var runtime = probed.IsSuccess
			? probed.Value
			: new RuntimeProbePayload { Detail = probed.Failure!.Text };
		report["runtime"] = JsonSerializer.SerializeToNode(runtime, PanelJson.Options);
		if (runtime.Dotnet.Length == 0)
		{
			blockers.Add(PhraseJsonConverter.ToNode(Phrase.From("Не найден dotnet. Vintage Story Server работает на .NET, установите среду выполнения.")));
		}
		else if (runtime.Majors.Count == 0)
		{
			warnings.Add(PhraseJsonConverter.ToNode(Phrase.From("dotnet найден, но список сред выполнения пуст — сервер может не запуститься.")));
		}

		// ADR-07: без setpriv --genconfig пришлось бы запускать от root. На Windows setpriv не нужен:
		// игру запускает служба-супервизор.
		if (probed.IsSuccess && runtime.Platform != "windows" && runtime.Setpriv.Length == 0)
		{
			blockers.Add(PhraseJsonConverter.ToNode(Phrase.From("Не найден setpriv (пакет util-linux). Без него панель не запускает игру: от root игра не работает никогда.")));
		}

		var listed = await registry.ListAsync(ct).ConfigureAwait(false);
		var instances = listed.IsSuccess
			? JsonSerializer.SerializeToNode(listed.Value, PanelJson.Options)!.AsObject()
			: new JsonObject { ["instances"] = new JsonArray(), ["writable"] = false, ["detail"] = PhraseJsonConverter.ToNode(listed.Failure!.Text) };
		report["instances"] = instances;
		if (!listed.IsSuccess || !listed.Value.Writable)
		{
			blockers.Add(PhraseJsonConverter.ToNode(Phrase.From("Реестр серверов доступен только на чтение. Выполните scripts/install.sh, чтобы перейти на конфигурацию версии 2.")));
		}

		var entries = listed.IsSuccess ? listed.Value.Instances : [];
		report["used_ports"] = new JsonArray([.. entries.Where(item => item.Port != 0).Select(item => item.Port).Distinct().Order().Select(port => (JsonNode?) port)]);
		report["used_ids"] = new JsonArray([.. entries.Select(item => item.Id).Order(StringComparer.Ordinal).Select(id => (JsonNode?) id)]);

		GamePackage? release = null;
		if (version is { } chosen)
		{
			var found = await catalogue.FindAsync(chosen.Text, refresh: false, ct).ConfigureAwait(false);
			if (found.IsSuccess)
			{
				release = found.Value;
			}
			else
			{
				blockers.Add(PhraseJsonConverter.ToNode(found.Failure!.Text));
			}
		}

		report["release"] = release?.ToJson();
		if (release is not null)
		{
			var needed = DiskRequirement.For(release.Size);
			report["required_free"] = needed;
			var disk = await host.DiskFreeAsync(ct).ConfigureAwait(false);
			var free = disk.IsSuccess ? disk.Value.Free : 0;
			report["free"] = free;
			if (DiskRequirement.Blocker(free, needed) is { } lacking)
			{
				blockers.Add(PhraseJsonConverter.ToNode(lacking));
			}
		}

		report["ok"] = blockers.Count == 0;
		return report;
	}

	/// <summary>
	/// Запустить создание в фоне. Аренда берётся здесь, синхронно с проверкой занятости.
	/// Идентификатор записи возвращается как ответ: по нему мастер найдёт операцию после
	/// перезапуска панели.
	/// </summary>
	public Task<Result<OperationId>> LaunchAsync(ServerSpec spec) => LaunchAsync(spec, null);

	public async Task<Result<OperationId>> LaunchAsync(ServerSpec spec, TransferPlan? transfer)
	{
		ArgumentNullException.ThrowIfNull(spec);
		await RecoverAsync().ConfigureAwait(false);

		lock (sync)
		{
			if (busy)
			{
				return Failure.Conflict("Уже выполняется обслуживание или другая операция");
			}

			var lease = coordinator.TryAcquire(spec.Id, LeasePurpose.Provision);
			if (lease is null)
			{
				return Failure.Conflict("Уже выполняется обслуживание или другая операция");
			}

			busy = true;
			var id = OperationId.New();
			var task = Task.Run(async () =>
			{
				using (lease)
				{
					try
					{
						await CreateAsync(spec, id, transfer).ConfigureAwait(false);
					}
					catch (Exception exception)
					{
						// Отказ до появления записи даёт запись «Сервер не создан», как run() в launch().
						var preflight = Operation.Start(id, OperationKind.ProvisionPreflight, Now());
						await FinishAsync(preflight, Failure.Unavailable(exception.Message)).ConfigureAwait(false);
					}
					finally
					{
						lock (sync)
						{
							busy = false;
						}
					}
				}
			});

			running.TryAdd(task, 0);
			_ = task.ContinueWith(done => running.TryRemove(done, out _), TaskScheduler.Default);
			return id;
		}
	}

	/// <summary>Повторяет <c>ProvisionManager.create</c>.</summary>
	private async Task CreateAsync(ServerSpec spec, OperationId id, TransferPlan? transfer)
	{
		var ct = CancellationToken.None;
		var operation = Operation.CreateProvision(id, spec.Id, spec.Version, Now());
		StagedFile? package = null;
		var registered = false;
		try
		{
			await SaveAsync(operation).ConfigureAwait(false);
			await EnsureAvailableAsync(spec, ct).ConfigureAwait(false);

			var release = Require(await catalogue.FindAsync(spec.Version, refresh: false, ct).ConfigureAwait(false));
			operation.Advance(new OperationStep(StepAction.Catalogue, StepResult.Ok) { Version = release.Version.Text, Size = release.Size });
			await SaveAsync(operation).ConfigureAwait(false);

			// Расширение берётся из имени файла в манифесте: пакет сервера для Windows — ZIP.
			package = StagingWriter.Reserve(
				StagedFile.Purpose.Game,
				release.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ".zip" : ".tar.gz");
			var written = Require(await downloader.DownloadAsync(release, staging.PathOf(package.Value), MaxPackageBytes, ct).ConfigureAwait(false));
			operation.Advance(new OperationStep(StepAction.Download, StepResult.Ok) { Bytes = written });
			await SaveAsync(operation).ConfigureAwait(false);

			if (transfer is not null)
			{
				var checkedTransfer = Require(await host.InspectTransferAsync(transfer.File, transfer.Sha256, transfer.Length, ct).ConfigureAwait(false));
				if (!string.Equals(checkedTransfer.ActiveWorld, transfer.ActiveWorld, StringComparison.Ordinal)
					|| !string.Equals(checkedTransfer.ManifestSha256, transfer.Inspection.ManifestSha256, StringComparison.Ordinal))
				{
					throw new ProvisionFailed(Failure.Conflict("Манифест пакета изменился после проверки"));
				}
			}

			var created = Require(transfer is null
				? await host.CreateInstanceAsync(spec, package.Value, ct).ConfigureAwait(false)
				: await host.ImportInstanceAsync(spec, package.Value, transfer.File, transfer.Sha256, transfer.Length, ct).ConfigureAwait(false));
			registered = true;
			operation.Advance(new OperationStep(StepAction.Create, StepResult.Ok) { Root = created.Root, TelemetryMod = created.TelemetryMod });
			await SaveAsync(operation).ConfigureAwait(false);

			// Неоткрытый игровой порт не отменяет созданного сервера: оператор откроет его сам.
			var service = Require(await host.InstallServiceAsync(spec.Id, ct).ConfigureAwait(false));
			operation.Advance(new OperationStep(StepAction.Service, service.FirewallWarning is null ? StepResult.Ok : StepResult.Warning)
			{
				Unit = created.Unit,
				Detail = service.FirewallWarning,
			});
			await SaveAsync(operation).ConfigureAwait(false);

			Require(await runtime.ServiceAsync(spec.Id, ServiceAction.Start, serviceTimeout, ct).ConfigureAwait(false));
			operation.Advance(new OperationStep(StepAction.Start, StepResult.Ok));
			await SaveAsync(operation).ConfigureAwait(false);

			var health = await WaitReadyAsync(spec.Id, ct).ConfigureAwait(false);
			operation.NoteHealth(health);
			var ready = Text(health["state"]) == "ready";
			operation.Advance(new OperationStep(StepAction.Ready, ready ? StepResult.Ok : StepResult.Warning) { Detail = PhraseJsonConverter.FromNode(health["detail"]) });
			await FinishAsync(operation, null).ConfigureAwait(false);
		}
		catch (Exception failure)
		{
			if (registered)
			{
				// Демон сам удаляет дерево только внутри instance-create. После этого сервер уже
				// существует, и что с ним делать, решает оператор.
				operation.Advance(new OperationStep(StepAction.Rollback, StepResult.Skipped)
				{
					Detail = Phrase.From("Сервер уже зарегистрирован; удалите его вручную, если он не нужен"),
				});
			}

			await FinishAsync(operation, failure is ProvisionFailed refused ? refused.Reason : Failure.Unavailable(failure.Message)).ConfigureAwait(false);
		}
		finally
		{
			if (package is { } staged)
			{
				staging.Discard(staged);
			}

			if (transfer is not null)
			{
				staging.Discard(transfer.File);
			}
		}
	}

	/// <summary>Конфликт в реестре проверяется до скачивания пакета в сотни мегабайт.</summary>
	private async Task EnsureAvailableAsync(ServerSpec spec, CancellationToken ct)
	{
		var registry = Require(await this.registry.ListAsync(ct).ConfigureAwait(false));
		if (!registry.Writable)
		{
			throw new ProvisionFailed(Failure.Conflict("Реестр серверов доступен только на чтение; выполните scripts/install.sh"));
		}

		foreach (var item in registry.Instances)
		{
			if (item.Id == spec.Id.Value)
			{
				throw new ProvisionFailed(Failure.Conflict($"Сервер {spec.Id.Value} уже существует"));
			}

			if (item.Port == spec.Port)
			{
				throw new ProvisionFailed(Failure.Conflict($"Порт {spec.Port} уже занят сервером {item.Id}"));
			}
		}
	}

	/// <summary>
	/// Готовность — свежая телеметрия только что запущенного процесса, а не просто запущенный
	/// процесс. Без мода телеметрии готовность не подтвердить, и это не ошибка: «ограниченная
	/// проверка» завершает ожидание сразу. Так же для юнита, который после ошибки
	/// перезапускается снова и снова: это происходит быстрее срока загрузки, и ждать до конца
	/// незачем.
	/// </summary>
	private async Task<JsonObject> WaitReadyAsync(InstanceId instance, CancellationToken ct)
	{
		var deadline = clock.GetUtcNow() + ReadinessTimeout;
		var health = new JsonObject { ["state"] = "unknown", ["detail"] = PhraseJsonConverter.ToNode(Phrase.From("Проверка ещё не выполнялась")) };
		ReadinessVerdict? previous = null;

		while (clock.GetUtcNow() < deadline)
		{
			var status = await runtime.StatusAsync(instance, ct).ConfigureAwait(false);
			if (!status.IsSuccess)
			{
				health = new JsonObject { ["state"] = "unknown", ["detail"] = PhraseJsonConverter.ToNode(status.Failure!.Text) };
			}
			else
			{
				var telemetry = await this.telemetry.ReadAsync(instance, ct).ConfigureAwait(false);
				previous = policy.Evaluate(status.Value, telemetry, WatchdogSettings.Default, previous, clock.GetUtcNow());
				health = AutomationService.HealthJson(previous);
				if (previous.State is HealthState.Ready or HealthState.Degraded)
				{
					return health;
				}

				if (status.Value.State == RuntimeState.Failed || status.Value.SubState == "auto-restart")
				{
					return new JsonObject
					{
						["state"] = "failed",
						["detail"] = PhraseJsonConverter.ToNode(Phrase.From("Служба сервера перезапускается после ошибки запуска; готовность недостижима. Проверьте журнал сервера.")),
					};
				}
			}

			await Task.Delay(ReadinessPoll, clock, ct).ConfigureAwait(false);
		}

		return health;
	}

	private async Task SaveAsync(Operation operation)
	{
		var snapshot = operation.ToJson();
		lock (sync)
		{
			current = snapshot;
		}

		await store.SaveCurrentAsync(operation, CancellationToken.None).ConfigureAwait(false);
	}

	/// <summary><c>_finish</c>: итог, история, очистка current, событие <c>provision.*</c>.</summary>
	private async Task FinishAsync(Operation operation, Failure? reason)
	{
		if (reason is null)
		{
			operation.Succeed(Now());
		}
		else
		{
			operation.Fail(reason, Now());
		}

		await store.ArchiveAsync(operation, CancellationToken.None).ConfigureAwait(false);
		lock (sync)
		{
			current = null;
		}

		// События создания идут на шину сервера по умолчанию.
		var target = InstanceId.Create(panelInstance.Value);
		if (!target.IsSuccess)
		{
			return;
		}

		var status = OperationWire.ToWire(operation.Status);
		await events.EmitAsync(
				target.Value,
				"provision." + status,
				operation.Status == OperationStatus.Success ? EventSeverity.Info : EventSeverity.Critical,
				operation.Title,
				operation.Detail,
				"provisioning",
				ct: CancellationToken.None)
			.ConfigureAwait(false);
	}

	private async Task<JsonObject?> RecoverOnceAsync()
	{
		var recovered = await store.RecoverAsync(Now(), CancellationToken.None).ConfigureAwait(false);
		if (recovered is not null)
		{
			Interrupted(log, PythonText.Of(recovered["id"]));
		}

		return recovered;
	}

	private long Now() => clock.GetUtcNow().ToUnixTimeSeconds();

	private static string Text(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

	private static T Require<T>(Result<T> result) =>
		result.IsSuccess ? result.Value : throw new ProvisionFailed(result.Failure!);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Создание сервера {Operation} прервано остановкой панели")]
	private static partial void Interrupted(ILogger logger, string operation);

	/// <summary>Отказ шага; его текст становится итогом операции.</summary>
	private sealed class ProvisionFailed(Failure reason) : Exception(reason.Message)
	{
		public Failure Reason { get; } = reason;
	}
}
