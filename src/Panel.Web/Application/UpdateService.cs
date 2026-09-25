using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Panel.Catalogues.Domain;
using Panel.Catalogues.Infrastructure;
using Panel.Content.Domain;
using Panel.Content.Ports;
using Panel.Contracts.ServerCommands;
using Panel.Maintenance.Domain;
using Panel.Maintenance.Infrastructure;
using Panel.Maintenance.Ports;
using Panel.Observability.Domain;
using Panel.Runtime.Domain;
using Panel.Runtime.Ports;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Ожидание готовности сервера после запуска. Отдельный порт, чтобы сценарии обновлений можно
/// было тестировать без автоматизации.
/// </summary>
public interface IReadinessWaiter
{
	Task<bool> WaitReadyAsync(InstanceId instance, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Обновление одного мода в пакете: страница ModDB и выбранный релиз.</summary>
public sealed record ModUpdateItem(string Identifier, long ReleaseId);

/// <summary>
/// Пакетное обновление модов, обновление программы сервера и откат к снимку.
/// </summary>
/// <remarks>
/// <para>
/// Каждая операция — <see cref="Operation"/>, которая записывается в <c>updates.json</c> после
/// каждого вызова демона, чтобы было видно, где операция оборвалась. Занятость определяется
/// арендой <see cref="MaintenanceCoordinator"/>, общей с плановым обслуживанием.
/// </para>
/// <para>
/// При штатной остановке панели операции не отменяются: остановка ждёт их завершения
/// (<see cref="StopAsync"/>). Наполовину заменённый каталог программы хуже любого ожидания.
/// </para>
/// </remarks>
public sealed partial class UpdateService : IHostedService
{
	private const int gameReadinessSeconds = 1200;

	private static readonly TimeSpan serviceTimeout = TimeSpan.FromSeconds(240);

	private readonly ConcurrentDictionary<InstanceId, Lazy<Task<Workspace>>> workspaces = new();
	private readonly ConcurrentDictionary<Task, byte> running = new();
	private readonly GameVersions versions;
	private readonly IServerRuntime runtime;
	private readonly IPlayerCommands players;
	private readonly IBackupStore backups;
	private readonly IModStore mods;
	private readonly IGamePackages packages;
	private readonly ModDbClient moddb;
	private readonly StagingWriter staging;
	private readonly MaintenanceCoordinator coordinator;
	private readonly IReadinessWaiter automation;
	private readonly EventBus events;
	private readonly PanelOptions options;
	private readonly TimeProvider clock;
	private readonly ILogger<UpdateService> log;

	public UpdateService(
		GameVersions versions,
		IServerRuntime runtime,
		IPlayerCommands players,
		IBackupStore backups,
		IModStore mods,
		IGamePackages packages,
		ModDbClient moddb,
		StagingWriter staging,
		MaintenanceCoordinator coordinator,
		IReadinessWaiter automation,
		EventBus events,
		PanelOptions options,
		TimeProvider clock,
		ILogger<UpdateService> log)
	{
		this.versions = versions;
		this.runtime = runtime;
		this.players = players;
		this.backups = backups;
		this.mods = mods;
		this.packages = packages;
		this.moddb = moddb;
		this.staging = staging;
		this.coordinator = coordinator;
		this.automation = automation;
		this.events = events;
		this.options = options;
		this.clock = clock;
		this.log = log;
	}

	public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	/// <summary>Остановка ждёт идущие операции.</summary>
	public Task StopAsync(CancellationToken cancellationToken) =>
		Task.WhenAll(running.Keys).WaitAsync(cancellationToken);

	/// <summary>
	/// Открыть рабочее место сервера. При первом открытии в процессе запись, оставшаяся от
	/// прошлого процесса, переносится в историю как прерванная.
	/// </summary>
	public async Task<JsonObject?> OpenAsync(InstanceId instance)
	{
		var workspace = await WorkspaceAsync(instance).ConfigureAwait(false);
		return workspace.Recovered;
	}

	// ----- чтение ---------------------------------------------------------------------

	public async Task<JsonObject> SnapshotAsync(InstanceId instance, CancellationToken ct)
	{
		var workspace = await WorkspaceAsync(instance).ConfigureAwait(false);
		var history = await workspace.Store.RecentAsync(30, ct).ConfigureAwait(false);

		lock (workspace.Sync)
		{
			return new JsonObject
			{
				["running"] = workspace.Running,
				["current"] = workspace.Current?.DeepClone(),
				["history"] = new JsonArray([.. history.Select(item => (JsonNode?) item)]),
			};
		}
	}

	/// <summary>
	/// Заявлена ли совместимость с целевой версией у точно установленных релизов. Первые 80
	/// модов, только включённые и с modid.
	/// </summary>
	public async Task<Result<JsonObject>> GamePlanAsync(InstanceId instance, string targetVersion, CancellationToken ct)
	{
		var listed = await mods.ListAsync(instance, ct).ConfigureAwait(false);
		if (!listed.IsSuccess)
		{
			return Result.Fail<JsonObject>(listed.Failure!);
		}

		var target = GameVersion.Create(targetVersion);
		var current = await versions.OfAsync(instance, ct).ConfigureAwait(false);
		var results = new List<JsonObject>();

		foreach (var mod in listed.Value.Mods.Take(80))
		{
			if (!mod.Enabled || string.IsNullOrEmpty(mod.ModId))
			{
				continue;
			}

			var version = mod.Version ?? string.Empty;
			var status = UpgradeStatus.Unverified;
			JsonNode tags = new JsonArray();
			string detail;

			var details = await moddb.DetailsAsync(mod.ModId, target, mod.DisplayName ?? string.Empty, ct).ConfigureAwait(false);
			if (!details.IsSuccess)
			{
				detail = $"Проверка недоступна: {details.Failure!.Message}";
			}
			else if (details.Value.Releases.FirstOrDefault(item => PythonText.Of(item.Json["version"]) == version) is not { } release)
			{
				detail = "Установленная версия не найдена в ModDB";
			}
			else
			{
				tags = release.Json["tags"]?.DeepClone() ?? new JsonArray();
				var compatible = release.Release.Compatibility.IsCompatible;
				status = compatible ? UpgradeStatus.Compatible : UpgradeStatus.Incompatible;
				detail = compatible ? "Совместимость заявлена" : "Для установленной версии совместимость не заявлена";
			}

			results.Add(new JsonObject
			{
				["modid"] = mod.ModId,
				["version"] = version,
				["status"] = UpgradePlan.ToWire(status),
				["tags"] = tags,
				["detail"] = detail,
			});
		}

		var blockers = results.Where(item => (string?) item["status"] == "incompatible").ToList();
		var warnings = results.Where(item => (string?) item["status"] == "unverified").ToList();

		return new JsonObject
		{
			["current_version"] = current.Text,
			["target_version"] = targetVersion,
			["branch_change"] = UpgradePlan.ChangesBranch(current, target),
			["mods"] = new JsonArray([.. results.Select(item => (JsonNode?) item)]),
			["blockers"] = new JsonArray([.. blockers.Select(item => item.DeepClone())]),
			["warnings"] = new JsonArray([.. warnings.Select(item => item.DeepClone())]),
			["allowed"] = blockers.Count == 0,
		};
	}

	// ----- запуск ---------------------------------------------------------------------

	public async Task<Result<string>> LaunchModsAsync(
		InstanceId instance,
		IReadOnlyList<ModUpdateItem> items,
		int readinessTimeoutSeconds)
	{
		if (items.Count is 0 or > 40)
		{
			return Failure.Conflict("Выберите от одного до 40 обновлений модов");
		}

		var workspace = await WorkspaceAsync(instance).ConfigureAwait(false);
		return Launch(instance, workspace, null, id => RunModsAsync(instance, workspace, id, items, readinessTimeoutSeconds));
	}

	/// <summary>Пакет уже в staging; с этого момента его удаляет операция, а не маршрут.</summary>
	public async Task<Result<string>> LaunchGameAsync(InstanceId instance, StagedFile package, string targetVersion)
	{
		var workspace = await WorkspaceAsync(instance).ConfigureAwait(false);
		return Launch(instance, workspace, package, id => RunGameAsync(instance, workspace, id, package, targetVersion));
	}

	public async Task<Result<string>> LaunchRollbackAsync(InstanceId instance, string snapshot)
	{
		var workspace = await WorkspaceAsync(instance).ConfigureAwait(false);
		return Launch(instance, workspace, null, id => RunRollbackAsync(instance, workspace, id, snapshot));
	}

	/// <summary>
	/// Аренда берётся здесь, синхронно с проверкой занятости, и только потом создаётся задача.
	/// Отказ внутри задачи до появления записи даёт запись «Обновление не началось».
	/// </summary>
	private Result<string> Launch(InstanceId instance, Workspace workspace, StagedFile? package, Func<OperationId, Task> body)
	{
		lock (workspace.Sync)
		{
			if (workspace.Running)
			{
				return Failure.Conflict("Уже выполняется обновление или обслуживание");
			}

			var lease = coordinator.TryAcquire(instance, LeasePurpose.Update);
			if (lease is null)
			{
				return Failure.Conflict("Уже выполняется обновление или обслуживание");
			}

			workspace.Running = true;
			var id = OperationId.New();
			var task = Task.Run(async () =>
			{
				using (lease)
				{
					try
					{
						await body(id).ConfigureAwait(false);
					}
					catch (Exception exception)
					{
						var reason = exception is UpdateFailed failed ? failed.Reason : Failure.Unavailable(exception.Message);
						var preflight = Operation.Start(id, OperationKind.Preflight, Now());
						await FinishAsync(instance, workspace, preflight, reason).ConfigureAwait(false);
					}
					finally
					{
						if (package is { } staged)
						{
							staging.Discard(staged);
						}

						lock (workspace.Sync)
						{
							workspace.Running = false;
						}
					}
				}
			});

			running.TryAdd(task, 0);
			_ = task.ContinueWith(done => running.TryRemove(done, out _), TaskScheduler.Default);
			return id.ToString();
		}
	}

	// ----- операции -------------------------------------------------------------------

	/// <summary>Всё скачать и проверить, остановить, копия офлайн, установить, запустить.</summary>
	private async Task RunModsAsync(
		InstanceId instance,
		Workspace workspace,
		OperationId id,
		IReadOnlyList<ModUpdateItem> items,
		int readinessTimeoutSeconds)
	{
		var ct = CancellationToken.None;
		var operation = Operation.Start(id, OperationKind.Mods, Now());
		var run = new Run(this, instance, workspace, operation);
		await run.SaveAsync().ConfigureAwait(false);

		var staged = new List<StagedFile>();
		var wasActive = false;
		SnapshotName? dataSnapshot = null;
		var changed = false;

		try
		{
			var game = await versions.OfAsync(instance, ct).ConfigureAwait(false);
			var existing = (await run.CallAsync(OperationPhase.Mods, () => mods.ListAsync(instance, ct)).ConfigureAwait(false)).Mods;

			// У мода без modid идентификатор "none".
			var enabledIds = existing.Where(mod => mod.Enabled).Select(mod => Fold(mod.ModId)).ToHashSet(StringComparer.Ordinal);
			var candidates = new List<DependencySubject>();

			foreach (var item in items)
			{
				var details = Require(await moddb.DetailsAsync(item.Identifier, game, string.Empty, ct).ConfigureAwait(false));
				var release = details.Releases.FirstOrDefault(entry => entry.Release.Id == item.ReleaseId);
				if (release is null || release.Release.IsPrerelease || !release.Release.Compatibility.IsCompatible)
				{
					throw new UpdateFailed(Failure.Invalid("Пакетная операция допускает только совместимые стабильные релизы"));
				}

				var download = Require(await moddb.ResolveDownloadAsync(item.Identifier, item.ReleaseId, game, ct).ConfigureAwait(false));
				var data = Require(await moddb
					.DownloadAsync(download.Url, (long) options.MaxUploadMegabytes * 1024 * 1024, ct)
					.ConfigureAwait(false));

				var info = Require(ModInfo.Read(data));
				if (!enabledIds.Contains(info.ModId) || candidates.Any(candidate => candidate.ModId == info.ModId))
				{
					throw new UpdateFailed(Failure.Invalid("Обновление должно заменять уникальный уже включённый мод"));
				}

				candidates.Add(new DependencySubject(info.ModId, info.Version, true, info.Dependencies));

				var file = StagingWriter.Reserve(StagedFile.Purpose.Mod, ".zip");
				staged.Add(file);
				using var source = new MemoryStream(data, writable: false);
				Require(await staging.WriteAsync(source, file, options.MaxUploadMegabytes, ct).ConfigureAwait(false));
			}

			var replaced = candidates.Select(candidate => candidate.ModId!).ToHashSet(StringComparer.Ordinal);
			var planned = existing
				.Where(mod => !replaced.Contains(Fold(mod.ModId)))
				.Select(mod => new DependencySubject(mod.ModId, mod.Version ?? string.Empty, mod.Enabled, mod.Dependencies))
				.Concat(candidates)
				.ToList();
			var issues = DependencyPolicy.Evaluate(planned, game);
			if (planned.Where((mod, index) => mod.Enabled && issues[index].Count > 0).Any())
			{
				throw new UpdateFailed(Failure.Invalid("План содержит отсутствующие, устаревшие или непроверенные зависимости"));
			}

			operation.Record(new OperationStep(StepAction.Download, StepResult.Ok) { Count = staged.Count });

			var status = await run.CallAsync(OperationPhase.Status, () => runtime.StatusAsync(instance, ct)).ConfigureAwait(false);
			wasActive = status.IsActive;
			if (wasActive)
			{
				await run.AnnounceAsync("Сервер обновляет моды и будет перезапущен.").ConfigureAwait(false);
				await run.ServiceAsync(ServiceAction.Stop).ConfigureAwait(false);
			}

			dataSnapshot = await run.OfflineBackupAsync().ConfigureAwait(false);

			foreach (var file in staged)
			{
				changed = true;
				var installed = await run.CallAsync(OperationPhase.ModInstall, () => mods.InstallAsync(instance, file, ct)).ConfigureAwait(false);
				operation.Record(new OperationStep(StepAction.Install, StepResult.Ok) { ModId = installed.ModId, Version = installed.Version });
			}

			if (wasActive)
			{
				await run.ServiceAsync(ServiceAction.Start).ConfigureAwait(false);
				if (!await automation.WaitReadyAsync(instance, TimeSpan.FromSeconds(readinessTimeoutSeconds), ct).ConfigureAwait(false))
				{
					throw new UpdateFailed(Failure.Unavailable("Сервер не подтвердил готовность после обновления модов"));
				}
			}

			await FinishAsync(instance, workspace, operation, null).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			try
			{
				if (changed && dataSnapshot is { } snapshot)
				{
					var status = await run.CallAsync(OperationPhase.Status, () => runtime.StatusAsync(instance, ct)).ConfigureAwait(false);
					if (status.IsActive)
					{
						await run.ServiceAsync(ServiceAction.Stop).ConfigureAwait(false);
					}

					await run.RestoreAsync(snapshot).ConfigureAwait(false);
					operation.Record(new OperationStep(StepAction.RollbackData, StepResult.Ok) { Name = snapshot.Value });
				}

				if (wasActive)
				{
					await run.ServiceAsync(ServiceAction.Start).ConfigureAwait(false);
				}
			}
			catch (Exception rollback)
			{
				operation.Record(new OperationStep(StepAction.Rollback, StepResult.Failed) { Detail = ReasonOf(rollback).Message });
			}

			await FinishAsync(instance, workspace, operation, ReasonOf(exception)).ConfigureAwait(false);
		}
		finally
		{
			foreach (var file in staged)
			{
				staging.Discard(file);
			}
		}
	}

	/// <summary>Проверить пакет, остановить, копия офлайн, установить со снимком, запустить.</summary>
	private async Task RunGameAsync(InstanceId instance, Workspace workspace, OperationId id, StagedFile package, string targetVersion)
	{
		var ct = CancellationToken.None;
		var previous = (await versions.OfAsync(instance, ct).ConfigureAwait(false)).Text;
		var operation = Operation.CreateGameUpdate(id, previous, targetVersion, Now());
		var run = new Run(this, instance, workspace, operation);
		await run.SaveAsync().ConfigureAwait(false);

		var wasActive = false;
		SnapshotName? program = null;
		SnapshotName? dataSnapshot = null;

		try
		{
			var inspection = await run.CallAsync(OperationPhase.GamePackageInspect, () => packages.InspectAsync(instance, package, ct)).ConfigureAwait(false);
			operation.Record(new OperationStep(StepAction.Validate, StepResult.Ok) { Files = inspection.Files });

			var status = await run.CallAsync(OperationPhase.Status, () => runtime.StatusAsync(instance, ct)).ConfigureAwait(false);
			wasActive = status.IsActive;
			if (wasActive)
			{
				await run.AnnounceAsync("Сервер обновляется и будет перезапущен.").ConfigureAwait(false);
				await run.ServiceAsync(ServiceAction.Stop).ConfigureAwait(false);
			}

			dataSnapshot = await run.OfflineBackupAsync().ConfigureAwait(false);

			var applied = await run.CallAsync(OperationPhase.GameUpdateApply, () => packages.ApplyAsync(instance, package, ct)).ConfigureAwait(false);
			program = Require(SnapshotName.Create(applied.Snapshot));
			operation.NoteProgramSnapshot(program.Value);
			operation.Record(new OperationStep(StepAction.Install, StepResult.Ok) { Snapshot = program.Value.Value });

			if (wasActive)
			{
				await run.ServiceAsync(ServiceAction.Start).ConfigureAwait(false);
				if (!await automation.WaitReadyAsync(instance, TimeSpan.FromSeconds(gameReadinessSeconds), ct).ConfigureAwait(false))
				{
					throw new UpdateFailed(Failure.Unavailable("Сервер не подтвердил готовность после обновления"));
				}
			}

			var recorded = await run.CallAsync(OperationPhase.PanelGameVersion, () => packages.RecordVersionAsync(instance, targetVersion, ct)).ConfigureAwait(false);
			operation.Record(new OperationStep(StepAction.Version, StepResult.Ok) { GameVersion = targetVersion });
			operation.NotePanelRestart(recorded.PanelRestartRequired);

			await FinishAsync(instance, workspace, operation, null).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			if (program is { } snapshot)
			{
				try
				{
					var status = await run.CallAsync(OperationPhase.Status, () => runtime.StatusAsync(instance, ct)).ConfigureAwait(false);
					if (status.IsActive)
					{
						await run.ServiceAsync(ServiceAction.Stop).ConfigureAwait(false);
					}

					await run.CallAsync(OperationPhase.GameUpdateRollback, () => packages.RollbackAsync(instance, snapshot, ct)).ConfigureAwait(false);
					if (dataSnapshot is { } data)
					{
						await run.RestoreAsync(data).ConfigureAwait(false);
					}

					await run.CallAsync(OperationPhase.PanelGameVersion, () => packages.RecordVersionAsync(instance, previous, ct)).ConfigureAwait(false);
					operation.Record(new OperationStep(StepAction.Rollback, StepResult.Ok) { Snapshot = snapshot.Value });
				}
				catch (Exception rollback)
				{
					operation.Record(new OperationStep(StepAction.Rollback, StepResult.Failed) { Detail = ReasonOf(rollback).Message });
				}
			}

			if (wasActive)
			{
				try
				{
					await run.ServiceAsync(ServiceAction.Start).ConfigureAwait(false);
				}
				catch (Exception restart) when (restart is not OutOfMemoryException)
				{
					// Неудачный запуск после отказа не меняет итог операции.
				}
			}

			await FinishAsync(instance, workspace, operation, ReasonOf(exception)).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Снимок программы и связанная с ним копия данных откатываются только вместе. Без копии
	/// откат не выполняется: программа была бы старой, а мир новым.
	/// </summary>
	private async Task RunRollbackAsync(InstanceId instance, Workspace workspace, OperationId id, string name)
	{
		var ct = CancellationToken.None;
		var source = (await workspace.Store.RecentAsync(1000, ct).ConfigureAwait(false))
			.FirstOrDefault(item => PythonText.Of(item["program_snapshot"]) == name && PythonText.Truthy(item["data_snapshot"]));
		if (source is null)
		{
			throw new UpdateFailed(Failure.Invalid("Для этого снимка нет связанной offline-копии данных; автоматический откат запрещён"));
		}

		var snapshot = Require(SnapshotName.Create(name));
		var data = Require(BackupName.Create(PythonText.Of(source["data_snapshot"])));

		var operation = Operation.Start(id, OperationKind.GameRollback, Now());
		var run = new Run(this, instance, workspace, operation);
		await run.SaveAsync().ConfigureAwait(false);

		var wasActive = false;
		try
		{
			// Опрос службы входит в операцию: при его отказе запись не должна остаться в current со
			// статусом «выполняется».
			wasActive = (await run.CallAsync(OperationPhase.Status, () => runtime.StatusAsync(instance, ct)).ConfigureAwait(false)).IsActive;

			await run.CallAsync(OperationPhase.BackupInspect, () => backups.InspectAsync(instance, data, ct)).ConfigureAwait(false);
			if (wasActive)
			{
				await run.ServiceAsync(ServiceAction.Stop).ConfigureAwait(false);
			}

			await run.CallAsync(OperationPhase.GameUpdateRollback, () => packages.RollbackAsync(instance, snapshot, ct)).ConfigureAwait(false);
			await run.CallAsync(OperationPhase.BackupRestore, () => backups.RestoreAsync(instance, data, ct)).ConfigureAwait(false);

			// Как source.get("previous_version", self.game_version): если ключ есть, берётся его значение.
			var version = source.ContainsKey("previous_version")
				? PythonText.Of(source["previous_version"]) is { Length: > 0 } text ? text : "None"
				: (await versions.OfAsync(instance, ct).ConfigureAwait(false)).Text;
			await run.CallAsync(OperationPhase.PanelGameVersion, () => packages.RecordVersionAsync(instance, version, ct)).ConfigureAwait(false);
			operation.NotePanelRestart(true);

			if (wasActive)
			{
				await run.ServiceAsync(ServiceAction.Start).ConfigureAwait(false);
				if (!await automation.WaitReadyAsync(instance, TimeSpan.FromSeconds(gameReadinessSeconds), ct).ConfigureAwait(false))
				{
					throw new UpdateFailed(Failure.Unavailable("Восстановленный сервер не подтвердил готовность"));
				}
			}

			await FinishAsync(instance, workspace, operation, null).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			var recovery = string.Empty;
			if (wasActive)
			{
				try
				{
					var status = await run.CallAsync(OperationPhase.Status, () => runtime.StatusAsync(instance, ct)).ConfigureAwait(false);
					if (!status.IsActive)
					{
						await run.ServiceAsync(ServiceAction.Start).ConfigureAwait(false);
					}
				}
				catch (Exception restart)
				{
					var message = ReasonOf(restart).Message;
					recovery = $"; сервер не удалось запустить: {message}";
					operation.Record(new OperationStep(StepAction.RestartAfterFailedRollback, StepResult.Failed) { Detail = message });
				}
			}

			var reason = ReasonOf(exception);
			await FinishAsync(instance, workspace, operation, reason with { Message = reason.Message + recovery }).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// <c>_finish</c>: итог, запись в историю, очистка <c>current</c>, событие
	/// <c>update.success</c> или <c>update.failed</c>.
	/// </summary>
	private async Task FinishAsync(InstanceId instance, Workspace workspace, Operation operation, Failure? reason)
	{
		if (reason is null)
		{
			operation.Succeed(Now());
		}
		else
		{
			operation.Fail(reason, Now());
		}

		await workspace.Store.ArchiveAsync(operation, CancellationToken.None).ConfigureAwait(false);
		lock (workspace.Sync)
		{
			workspace.Current = null;
		}

		var status = OperationWire.ToWire(operation.Status);
		await events.EmitAsync(
				instance,
				"update." + status,
				operation.Status == OperationStatus.Success ? EventSeverity.Info : EventSeverity.Critical,
				operation.Title,
				operation.Detail ?? string.Empty,
				"updates",
				ct: CancellationToken.None)
			.ConfigureAwait(false);
	}

	private long Now() => clock.GetUtcNow().ToUnixTimeSeconds();

	private static string Fold(string? modId) => (modId ?? "None").ToLowerInvariant();

	private static T Require<T>(Result<T> result) =>
		result.IsSuccess ? result.Value : throw new UpdateFailed(result.Failure!);

	private static Failure ReasonOf(Exception exception) =>
		exception is UpdateFailed failed ? failed.Reason : Failure.Unavailable(exception.Message);

	private Task<Workspace> WorkspaceAsync(InstanceId instance) =>
		workspaces.GetOrAdd(instance, id => new Lazy<Task<Workspace>>(() => OpenWorkspaceAsync(id))).Value;

	private async Task<Workspace> OpenWorkspaceAsync(InstanceId instance)
	{
		var store = new OperationStore(options.InstanceDirectory(instance));
		var recovered = await store.RecoverAsync(Now(), CancellationToken.None).ConfigureAwait(false);
		if (recovered is not null)
		{
			Interrupted(log, instance.Value, PythonText.Of(recovered["id"]));
		}

		return new Workspace(store) { Recovered = recovered };
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "Операция {Operation} на сервере {Instance} прервана остановкой панели")]
	private static partial void Interrupted(ILogger logger, string instance, string operation);

	/// <summary>Отказ шага; его текст становится итогом операции.</summary>
	private sealed class UpdateFailed(Failure reason) : Exception(reason.Message)
	{
		public Failure Reason { get; } = reason;
	}

	private sealed class Workspace(OperationStore store)
	{
		public object Sync { get; } = new();

		public OperationStore Store { get; } = store;

		public bool Running { get; set; }

		/// <summary>Последняя записанная форма текущей операции; её показывает <c>/api/updates</c>.</summary>
		public JsonObject? Current { get; set; }

		public JsonObject? Recovered { get; init; }
	}

	/// <summary>
	/// Шаги одной операции. <see cref="CallAsync{T}"/>: этап, запись <c>updates.json</c>, вызов
	/// демона, а при отказе исключение с его текстом.
	/// </summary>
	private sealed class Run(UpdateService owner, InstanceId instance, Workspace workspace, Operation operation)
	{
		public async Task SaveAsync()
		{
			var snapshot = operation.ToJson();
			lock (workspace.Sync)
			{
				workspace.Current = snapshot;
			}

			await workspace.Store.SaveCurrentAsync(operation, CancellationToken.None).ConfigureAwait(false);
		}

		public async Task<T> CallAsync<T>(OperationPhase phase, Func<Task<Result<T>>> call)
		{
			operation.Enter(phase);
			await SaveAsync().ConfigureAwait(false);
			return Require(await call().ConfigureAwait(false));
		}

		public Task<Done> ServiceAsync(ServiceAction action) =>
			CallAsync(OperationPhase.Service, () => owner.runtime.ServiceAsync(instance, action, serviceTimeout, CancellationToken.None));

		/// <summary>Предупреждение игрокам. Его отказ операцию не останавливает, как <c>except Exception: pass</c>.</summary>
		public async Task AnnounceAsync(string text)
		{
			try
			{
				var command = PlayerCommand.Create("announce", string.Empty, text);
				if (command.IsSuccess)
				{
					await CallAsync(OperationPhase.PlayerAction, () => owner.players.SendAsync(instance, command.Value, CancellationToken.None))
						.ConfigureAwait(false);
				}
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				// Сервер без консоли не может предупредить игроков, но обновление продолжается.
			}
		}

		public async Task<SnapshotName> OfflineBackupAsync()
		{
			var backup = await CallAsync(OperationPhase.BackupCreateOffline, () => owner.backups.CreateAsync(instance, offline: true, CancellationToken.None))
				.ConfigureAwait(false);
			var name = Require(SnapshotName.Create(backup.Name));
			operation.NoteDataSnapshot(name);
			operation.Record(new OperationStep(StepAction.Backup, StepResult.Ok) { Name = backup.Name });
			return name;
		}

		public async Task RestoreAsync(SnapshotName snapshot)
		{
			var name = Require(BackupName.Create(snapshot.Value));
			await CallAsync(OperationPhase.BackupRestore, () => owner.backups.RestoreAsync(instance, name, CancellationToken.None)).ConfigureAwait(false);
		}
	}
}

/// <summary>
/// <c>modinfo.json</c> из скачанного архива: modid в нижнем регистре, версия и зависимости.
/// Разбор строгий.
/// </summary>
internal sealed record ModInfo(string ModId, string Version, IReadOnlyDictionary<string, string> Dependencies)
{
	public static Result<ModInfo> Read(byte[] archive)
	{
		try
		{
			using var zip = new ZipArchive(new MemoryStream(archive, writable: false), ZipArchiveMode.Read);
			var entry = zip.GetEntry("modinfo.json");
			if (entry is null)
			{
				return Failure.Invalid("В архиве мода нет modinfo.json");
			}

			using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
			if (JsonNode.Parse(reader.ReadToEnd()) is not JsonObject info)
			{
				return Failure.Invalid("modinfo.json не содержит JSON-объекта");
			}

			var dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
			if (info["dependencies"] is JsonObject declared)
			{
				foreach (var (key, value) in declared)
				{
					dependencies[key] = PythonText.Of(value);
				}
			}

			// Как str(info.get("modid", "")): нет ключа — пустая строка, null — "None".
			var modId = !info.ContainsKey("modid") ? string.Empty : info["modid"] is null ? "None" : PythonText.Of(info["modid"]);
			return new ModInfo(modId.ToLowerInvariant(), PythonText.Of(info["version"]), dependencies);
		}
		catch (Exception exception) when (exception is InvalidDataException or JsonException or IOException)
		{
			return Failure.Invalid($"Архив мода не читается: {exception.Message}");
		}
	}
}
