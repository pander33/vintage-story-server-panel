using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Agent.Content;
using Panel.Agent.Runtime;
using Panel.Content.Domain;
using Panel.Content.Ports;
using Panel.Contracts;
using Panel.Contracts.Agent;
using Panel.Contracts.Content;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Transport;

/// <summary>
/// Операции над содержимым сервера: моды, миры, резервные копии.
/// </summary>
/// <remarks>
/// Вынесено из <see cref="AgentDispatcher"/>, потому что это 28 операций из примерно 40.
/// Каждый аргумент проверяется здесь независимо от проверок веб-процесса.
/// </remarks>
public sealed class ContentOperations : IServerOperations
{
	private readonly ModBackend mods;
	private readonly WorldBackend worlds;
	private readonly BackupBackend backups;
	private readonly StagingArea staging;
	private readonly IServiceBackend service;
	private readonly HistoryBackend history;
	private readonly OperationTracker tracker;
	private readonly WorldSchemaReader schemas;

	public ContentOperations(
		ModBackend mods,
		WorldBackend worlds,
		BackupBackend backups,
		IServiceBackend service,
		HistoryBackend history,
		StagingArea staging,
		OperationTracker tracker,
		WorldSchemaReader schemas)
	{
		ArgumentNullException.ThrowIfNull(tracker);
		this.tracker = tracker;
		ArgumentNullException.ThrowIfNull(schemas);
		this.schemas = schemas;
		ArgumentNullException.ThrowIfNull(staging);
		this.staging = staging;
		ArgumentNullException.ThrowIfNull(history);
		this.history = history;
		ArgumentNullException.ThrowIfNull(mods);
		ArgumentNullException.ThrowIfNull(worlds);
		ArgumentNullException.ThrowIfNull(backups);
		ArgumentNullException.ThrowIfNull(service);

		this.mods = mods;
		this.worlds = worlds;
		this.backups = backups;
		this.service = service;
	}

	/// <summary>Относится ли операция к содержимому.</summary>
	public static bool Handles(string op) => op is
		AgentProtocol.OpMods or AgentProtocol.OpModInstall or AgentProtocol.OpModToggle
		or AgentProtocol.OpModDelete or AgentProtocol.OpModRollback or AgentProtocol.OpModProfileSave
		or AgentProtocol.OpModProfileApply or AgentProtocol.OpModProfileDelete or AgentProtocol.OpModConfigs
		or AgentProtocol.OpModConfigGet or AgentProtocol.OpModConfigPut
		or AgentProtocol.OpWorlds or AgentProtocol.OpWorldImport or AgentProtocol.OpWorldExport
		or AgentProtocol.OpWorldClone or AgentProtocol.OpWorldActivate or AgentProtocol.OpWorldCreate
		or AgentProtocol.OpWorldPutAway or AgentProtocol.OpWorldStored or AgentProtocol.OpWorldRestore
		or AgentProtocol.OpWorldStoredDelete
		or AgentProtocol.OpBackups or AgentProtocol.OpBackupCreate or AgentProtocol.OpBackupInspect
		or AgentProtocol.OpBackupRestore or AgentProtocol.OpBackupDelete or AgentProtocol.OpBackupRestoreWorld
		or AgentProtocol.OpBackupRetention or AgentProtocol.OpTransferExport or AgentProtocol.OpOperation
		or AgentProtocol.OpHistory or AgentProtocol.OpHistoryDelete or AgentProtocol.OpHistoryPrune;

	bool IServerOperations.Serves(string op) => Handles(op);

	public async Task<AgentReply> ExecuteAsync(AgentRequest request, RegisteredServer server, CancellationToken ct) =>
		request.Op switch
		{
			AgentProtocol.OpMods => Reply(mods.List(server).Map(ContentMapping.ToPayload)),
			AgentProtocol.OpModInstall => Reply(await WithStaged(
				request, 0, staged => mods.InstallAsync(server, staged, ct)).ConfigureAwait(false),
				ContentMapping.ToPayload),
			AgentProtocol.OpModToggle => Reply(
				ModArchiveName.Create(Argument(request, 0))
					.Bind(mod => mods.Toggle(server, mod))
					.Map(ContentMapping.ToPayload)),
			AgentProtocol.OpModDelete => Reply(
				ModArchiveName.Create(Argument(request, 0))
					.Bind(mod => mods.Delete(server, mod))
					.Map(ContentMapping.ToPayload)),
			AgentProtocol.OpModRollback => await ModRollbackAsync(request, server, ct).ConfigureAwait(false),
			AgentProtocol.OpModProfileSave => Reply(
				ModProfileName.Create(Argument(request, 0))
					.Bind(profile => mods.SaveProfile(server, profile))
					.Map(ContentMapping.ToPayload)),
			AgentProtocol.OpModProfileApply => Reply(
				ModProfileName.Create(Argument(request, 0))
					.Bind(profile => mods.ApplyProfile(server, profile))
					.Map(ContentMapping.ToPayload)),
			AgentProtocol.OpModProfileDelete => Reply(
				ModProfileName.Create(Argument(request, 0))
					.Bind(profile => mods.DeleteProfile(server, profile))
					.Map(_ => new JsonObject { ["ok"] = true })),
			AgentProtocol.OpModConfigs => Reply(mods.ListConfigs(server).Map(ContentMapping.ToPayload)),
			AgentProtocol.OpModConfigGet => await ModConfigGetAsync(request, server, ct).ConfigureAwait(false),
			AgentProtocol.OpModConfigPut => await ModConfigPutAsync(request, server, ct).ConfigureAwait(false),

			AgentProtocol.OpWorlds => Reply(
				worlds.List(server, await IsRunningAsync(server, ct).ConfigureAwait(false))
					.Map(ContentMapping.ToPayload)),
			AgentProtocol.OpWorldImport => await WorldImportAsync(request, server, ct).ConfigureAwait(false),
			AgentProtocol.OpWorldExport => await WorldExportAsync(request, server, ct).ConfigureAwait(false),
			AgentProtocol.OpWorldClone => await WorldCloneAsync(request, server, ct).ConfigureAwait(false),
			AgentProtocol.OpWorldActivate => await WorldActivateAsync(request, server, ct).ConfigureAwait(false),
			AgentProtocol.OpWorldCreate => await WorldCreateAsync(request, server, ct).ConfigureAwait(false),
			AgentProtocol.OpWorldPutAway => await WorldPutAwayAsync(request, server, ct).ConfigureAwait(false),
			AgentProtocol.OpWorldStored => Reply(worlds.ListStored(server).Map(ContentMapping.ToPayload)),
			AgentProtocol.OpWorldRestore => await WorldRestoreAsync(request, server, ct).ConfigureAwait(false),
			AgentProtocol.OpWorldStoredDelete => WorldStoredDelete(request, server),

			AgentProtocol.OpBackups => Reply(backups.List(server).Map(ContentMapping.ToPayload)),
			AgentProtocol.OpBackupCreate => await BackupCreateAsync(request, server, ct).ConfigureAwait(false),
			AgentProtocol.OpTransferExport => await TransferExportAsync(request, server, ct).ConfigureAwait(false),
			AgentProtocol.OpOperation => Reply(Result.Ok(tracker.Snapshot(server.Id.Value))),
			AgentProtocol.OpBackupInspect => await BackupInspectAsync(request, server, ct).ConfigureAwait(false),
			AgentProtocol.OpBackupRestore => await BackupRestoreAsync(request, server, ct).ConfigureAwait(false),
			AgentProtocol.OpBackupDelete => await BackupDeleteAsync(request, server, ct).ConfigureAwait(false),
			AgentProtocol.OpBackupRestoreWorld => await BackupRestoreWorldAsync(request, server, ct)
				.ConfigureAwait(false),
			AgentProtocol.OpBackupRetention => Retention(request, server),
			AgentProtocol.OpHistory => Reply(Result.Ok(history.List(server))),
			AgentProtocol.OpHistoryDelete => Reply(
				HistoryStoreWire.Parse(Argument(request, 0)).Bind(store =>
					HistoryKey.Create(Argument(request, 1)).Bind(key => history.Delete(server, store, key)))),
			AgentProtocol.OpHistoryPrune => Reply(
				HistoryStoreWire.Parse(Argument(request, 0)).Bind(store =>
					HistoryKeep.Create(Number(Argument(request, 1))).Map(keep => history.Prune(server, store, keep)))),

			_ => AgentReply.Failure("op", $"Неизвестная операция: {request.Op}"),
		};

	private async Task<AgentReply> ModRollbackAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
	{
		var mod = ModId.Create(Argument(request, 0));
		if (!mod.IsSuccess)
		{
			return Failed(mod.Failure!);
		}

		var archived = ModArchiveName.Create(Argument(request, 1));
		if (!archived.IsSuccess)
		{
			return Failed(archived.Failure!);
		}

		var rolled = await mods.RollbackAsync(server, mod.Value, archived.Value, ct).ConfigureAwait(false);
		return Reply(rolled, ContentMapping.ToPayload);
	}

	private async Task<AgentReply> ModConfigGetAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
	{
		var name = ModConfigName.Create(Argument(request, 0));
		if (!name.IsSuccess)
		{
			return Failed(name.Failure!);
		}

		var document = await mods.ReadConfigAsync(server, name.Value, ct).ConfigureAwait(false);
		return Reply(document, ContentMapping.ToPayload);
	}

	private async Task<AgentReply> ModConfigPutAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
	{
		var name = ModConfigName.Create(Argument(request, 0));
		if (!name.IsSuccess)
		{
			return Failed(name.Failure!);
		}

		var staged = StagedFile.Create(Argument(request, 1));
		if (!staged.IsSuccess)
		{
			return Failed(staged.Failure!);
		}

		var written = await mods.WriteConfigAsync(server, name.Value, staged.Value, ct).ConfigureAwait(false);
		return Reply(written.Map(_ => new JsonObject { ["ok"] = true, ["restart_required"] = true }));
	}

	private async Task<AgentReply> WorldImportAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
	{
		var staged = StagedFile.Create(Argument(request, 0));
		if (!staged.IsSuccess)
		{
			return Failed(staged.Failure!);
		}

		var name = WorldFileName.Create(Argument(request, 1));
		if (!name.IsSuccess)
		{
			return Failed(name.Failure!);
		}

		var running = await IsRunningAsync(server, ct).ConfigureAwait(false);
		var imported = await worlds
			.ImportAsync(server, staged.Value, name.Value, running, ct)
			.ConfigureAwait(false);

		return Reply(imported.Map(world => new WorldChangePayload
		{
			Name = world.Name.Value,
			Size = world.Size,
			RestartRequired = false,
		}));
	}

	private async Task<AgentReply> WorldExportAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
	{
		var name = WorldFileName.Create(Argument(request, 0));
		if (!name.IsSuccess)
		{
			return Failed(name.Failure!);
		}

		var staged = StagedFile.Create(Argument(request, 1));
		if (!staged.IsSuccess)
		{
			return Failed(staged.Failure!);
		}

		var exported = await worlds.ExportAsync(server, name.Value, staged.Value, ct).ConfigureAwait(false);

		return Reply(exported.Map(item => new WorldExportPayload
		{
			Name = item.Name.Value,
			Staged = item.Staged.Name,
			Size = item.Size,
		}));
	}

	private async Task<AgentReply> WorldCloneAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
	{
		var source = WorldFileName.Create(Argument(request, 0));
		if (!source.IsSuccess)
		{
			return Failed(source.Failure!);
		}

		var target = WorldFileName.Create(Argument(request, 1));
		if (!target.IsSuccess)
		{
			return Failed(target.Failure!);
		}

		var running = await IsRunningAsync(server, ct).ConfigureAwait(false);
		var cloned = worlds.Clone(server, source.Value, target.Value, running);

		return Reply(cloned.Map(world => new WorldChangePayload
		{
			Name = world.Name.Value,
			Size = world.Size,
			RestartRequired = false,
		}));
	}

	private async Task<AgentReply> WorldActivateAsync(
		AgentRequest request,
		RegisteredServer server,
		CancellationToken ct)
	{
		var name = WorldFileName.Create(Argument(request, 0));
		if (!name.IsSuccess)
		{
			return Failed(name.Failure!);
		}

		var running = await IsRunningAsync(server, ct).ConfigureAwait(false);
		var activated = await worlds.ActivateAsync(server, name.Value, running, ct).ConfigureAwait(false);

		return Reply(activated.Map(_ => new WorldChangePayload { Active = name.Value.Value }));
	}

	/// <summary>
	/// Стиль, который объявляет схема этого сервера (игра и включённые моды), с её ключом подписи.
	/// Без схемы — только встроенные стили игры с прежним ключом <c>"preset-" + код</c>.
	/// </summary>
	private Result<PlayStyle> Declared(PlayStyle requested, RegisteredServer server)
	{
		var declared = schemas.Read(server).Value.Playstyles;
		if (declared.Count == 0)
		{
			return PlayStyle.Builtin.Contains(requested.Code, StringComparer.Ordinal)
				? requested
				: Failure.Invalid("Недопустимый стиль игры");
		}

		var found = declared.FirstOrDefault(style => string.Equals(style.Code, requested.Code, StringComparison.Ordinal));
		return found is null
			? Failure.Invalid("Недопустимый стиль игры")
			: PlayStyle.CreateDeclared(found.Code, found.LanguageCode);
	}

	private async Task<AgentReply> WorldCreateAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
	{
		var name = WorldFileName.Create(Argument(request, 0));
		if (!name.IsSuccess)
		{
			return Failed(name.Failure!);
		}

		var style = PlayStyle.Create(Argument(request, 3)).Bind(requested => Declared(requested, server));
		if (!style.IsSuccess)
		{
			return Failed(style.Failure!);
		}

		var display = Text(Argument(request, 1), 80);
		if (!display.IsSuccess)
		{
			return Failed(display.Failure!);
		}

		var seed = Text(Argument(request, 2), 80, allowEmpty: true);
		if (!seed.IsSuccess)
		{
			return Failed(seed.Failure!);
		}

		var running = await IsRunningAsync(server, ct).ConfigureAwait(false);
		var plan = new WorldPlan(name.Value, display.Value, seed.Value, style.Value);
		var created = await worlds.CreateAsync(server, plan, running, ct).ConfigureAwait(false);

		return Reply(created.Map(_ => new WorldChangePayload
		{
			Active = name.Value.Value,
			PendingCreation = true,
		}));
	}

	private async Task<AgentReply> WorldPutAwayAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
	{
		var name = WorldFileName.Create(Argument(request, 0));
		if (!name.IsSuccess)
		{
			return Failed(name.Failure!);
		}

		var disposition = WorldDispositionWire.FromWire(Argument(request, 1));
		if (!disposition.IsSuccess)
		{
			return Failed(disposition.Failure!);
		}

		var running = await IsRunningAsync(server, ct).ConfigureAwait(false);
		var stored = worlds.PutAway(server, name.Value, disposition.Value, running);

		return Reply(stored.Map(world => new WorldChangePayload
		{
			Name = name.Value.Value,
			Mode = WorldDispositionWire.ToWire(world.Disposition),
			StoredAs = world.StoredName.Value,
			RestartRequired = false,
		}));
	}

	private async Task<AgentReply> WorldRestoreAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
	{
		var disposition = WorldDispositionWire.FromWire(Argument(request, 0));
		if (!disposition.IsSuccess)
		{
			return Failed(disposition.Failure!);
		}

		var stored = StoredWorldName.Create(Argument(request, 1));
		if (!stored.IsSuccess)
		{
			return Failed(stored.Failure!);
		}

		var target = WorldFileName.Create(Argument(request, 2));
		if (!target.IsSuccess)
		{
			return Failed(target.Failure!);
		}

		var running = await IsRunningAsync(server, ct).ConfigureAwait(false);
		var restored = await worlds
			.RestoreStoredAsync(server, disposition.Value, stored.Value, target.Value, running, ct)
			.ConfigureAwait(false);

		return Reply(restored.Map(world => new WorldChangePayload
		{
			Name = world.Name.Value,
			Mode = WorldDispositionWire.ToWire(disposition.Value),
			StoredAs = stored.Value.Value,
			Size = world.Size,
			RestartRequired = false,
		}));
	}

	private AgentReply WorldStoredDelete(AgentRequest request, RegisteredServer server)
	{
		var disposition = WorldDispositionWire.FromWire(Argument(request, 0));
		if (!disposition.IsSuccess)
		{
			return Failed(disposition.Failure!);
		}

		var stored = StoredWorldName.Create(Argument(request, 1));
		if (!stored.IsSuccess)
		{
			return Failed(stored.Failure!);
		}

		var freed = worlds.DeleteStored(server, disposition.Value, stored.Value);

		return Reply(freed.Map(bytes => new WorldChangePayload
		{
			Name = stored.Value.Value,
			Mode = WorldDispositionWire.ToWire(disposition.Value),
			Freed = bytes,
			RestartRequired = false,
		}));
	}

	/// <summary>
	/// Снять копию. Службу останавливает и запускает демон, чтобы между остановкой и упаковкой
	/// никто не успел запустить сервер.
	/// </summary>
	private async Task<AgentReply> BackupCreateAsync(
		AgentRequest request,
		RegisteredServer server,
		CancellationToken ct)
	{
		var offline = Argument(request, 0) == "offline";
		var running = await IsRunningAsync(server, ct).ConfigureAwait(false);

		if (offline && running)
		{
			return Failed(Failure.Conflict("Для обслуживающей копии сервер должен быть остановлен"));
		}

		var stop = running && !offline;
		using var scope = tracker.Begin(server.Id.Value, "backup", OperationStages.Backup, OperationStages.BackupTitle);
		if (stop)
		{
			await service.ServiceAsync(server, "stop", TimeSpan.FromSeconds(180), ct).ConfigureAwait(false);
			if (await IsRunningAsync(server, ct).ConfigureAwait(false))
			{
				return Failed(Failure.Conflict("Сервер не остановился перед созданием пакета"));
			}
		}

		try
		{
			var created = await backups
				.CreateAsync(server, BackupName.RoutinePrefix, ct, scope)
				.ConfigureAwait(false);

			return Reply(created.Map(backup => new BackupChangePayload
			{
				Name = backup.Name.Value,
				Size = backup.Size,
			}));
		}
		finally
		{
			if (stop)
			{
				scope.Enter(OperationStages.Start);
				await service.ServiceAsync(server, "start", TimeSpan.FromSeconds(180), ct).ConfigureAwait(false);
			}
		}
	}

	private async Task<AgentReply> TransferExportAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
	{
		var staged = StagedFile.Create(Argument(request, 0));
		if (!staged.IsSuccess || !staged.Value.Name.StartsWith(StagedFile.Purpose.Transfer, StringComparison.Ordinal)
			|| !staged.Value.Name.EndsWith(".tar.gz", StringComparison.Ordinal))
		{
			return Failed(Failure.Invalid("Недопустимое имя пакета для выгрузки"));
		}

		var path = await staging.ReserveAsync(staged.Value, ct).ConfigureAwait(false);
		if (!path.IsSuccess)
		{
			return Failed(path.Failure!);
		}

		var running = await IsRunningAsync(server, ct).ConfigureAwait(false);
		using var scope = tracker.Begin(server.Id.Value, "export", OperationStages.Export, OperationStages.ExportTitle);
		try
		{
			if (running)
			{
				await service.ServiceAsync(server, "stop", TimeSpan.FromSeconds(180), ct).ConfigureAwait(false);
				if (await IsRunningAsync(server, ct).ConfigureAwait(false))
				{
					return Failed(Failure.Conflict("Сервер не остановился перед созданием пакета"));
				}
			}

			var exported = await backups.ExportAsync(server, path.Value, ct, scope).ConfigureAwait(false);
			return Reply(exported.Map(size => new WorldExportPayload
			{
				Name = $"server-{server.Id.Value}.tar.gz",
				Staged = staged.Value.Name,
				Size = size,
			}));
		}
		finally
		{
			if (running)
			{
				scope.Enter(OperationStages.Start);
				await service.ServiceAsync(server, "start", TimeSpan.FromSeconds(180), ct).ConfigureAwait(false);
			}
		}
	}

	private async Task<AgentReply> BackupInspectAsync(
		AgentRequest request,
		RegisteredServer server,
		CancellationToken ct)
	{
		var name = BackupName.Create(Argument(request, 0));
		if (!name.IsSuccess)
		{
			return Failed(name.Failure!);
		}

		var contents = await backups.InspectAsync(server, name.Value, ct).ConfigureAwait(false);
		return Reply(contents.Map(value => ContentMapping.ToPayload(name.Value, value)));
	}

	private async Task<AgentReply> BackupRestoreAsync(
		AgentRequest request,
		RegisteredServer server,
		CancellationToken ct)
	{
		var name = BackupName.Create(Argument(request, 0));
		if (!name.IsSuccess)
		{
			return Failed(name.Failure!);
		}

		var running = await IsRunningAsync(server, ct).ConfigureAwait(false);
		using var scope = tracker.Begin(server.Id.Value, "restore", OperationStages.Restore, OperationStages.RestoreTitle);
		if (running)
		{
			scope.Enter(OperationStages.Stop);
			await service.ServiceAsync(server, "stop", TimeSpan.FromSeconds(180), ct).ConfigureAwait(false);
			if (await IsRunningAsync(server, ct).ConfigureAwait(false))
			{
				return Failed(Failure.Conflict("Сервер не остановился перед восстановлением"));
			}
		}

		try
		{
			var restored = await backups.RestoreAsync(server, name.Value, ct, scope).ConfigureAwait(false);

			return Reply(restored.Map(safety => new BackupChangePayload
			{
				Restored = name.Value.Value,
				SafetyBackup = safety.Value,
			}));
		}
		finally
		{
			if (running)
			{
				scope.Enter(OperationStages.Start);
				await service.ServiceAsync(server, "start", TimeSpan.FromSeconds(180), ct).ConfigureAwait(false);
			}
		}
	}

	private async Task<AgentReply> BackupDeleteAsync(
		AgentRequest request,
		RegisteredServer server,
		CancellationToken ct)
	{
		var name = BackupName.Create(Argument(request, 0));
		if (!name.IsSuccess)
		{
			return Failed(name.Failure!);
		}

		var freed = await backups.DeleteAsync(server, name.Value, ct).ConfigureAwait(false);
		var remaining = backups.List(server);

		return Reply(freed.Map(bytes => new BackupChangePayload
		{
			Name = name.Value.Value,
			Freed = bytes,
			Remaining = remaining.IsSuccess ? remaining.Value.Backups.Count : null,
		}));
	}

	private async Task<AgentReply> BackupRestoreWorldAsync(
		AgentRequest request,
		RegisteredServer server,
		CancellationToken ct)
	{
		var backup = BackupName.Create(Argument(request, 0));
		if (!backup.IsSuccess)
		{
			return Failed(backup.Failure!);
		}

		var world = Text(Argument(request, 1), WorldFileName.MaxLength);
		if (!world.IsSuccess)
		{
			return Failed(world.Failure!);
		}

		var target = WorldFileName.Create(Argument(request, 2));
		if (!target.IsSuccess)
		{
			return Failed(target.Failure!);
		}

		if (await IsRunningAsync(server, ct).ConfigureAwait(false))
		{
			return Failed(Failure.Conflict("Остановите сервер Vintage Story перед изменением миров"));
		}

		using var scope = tracker.Begin(server.Id.Value, "restore-world", OperationStages.RestoreWorld, OperationStages.RestoreWorldTitle);
		var restored = await backups
			.RestoreWorldAsync(server, backup.Value, world.Value, target.Value, ct, scope)
			.ConfigureAwait(false);

		return Reply(restored.Map(item => new WorldChangePayload
		{
			Name = item.Name.Value,
			Size = item.Size,
			RestartRequired = false,
		}));
	}

	/// <summary>
	/// Политика удержания: предпросмотр или выполнение. План строится и применяется по одному
	/// и тому же перечню копий, без повторного чтения.
	/// </summary>
	private AgentReply Retention(AgentRequest request, RegisteredServer server)
	{
		var policy = RetentionPolicy.Create(
			Number(Argument(request, 0)),
			Number(Argument(request, 1)),
			Number(Argument(request, 2)));

		if (!policy.IsSuccess)
		{
			return Failed(policy.Failure!);
		}

		var mode = Argument(request, 3);
		if (mode is not ("preview" or "apply"))
		{
			return Failed(Failure.Invalid("Режим удержания — preview либо apply"));
		}

		var set = backups.List(server);
		if (!set.IsSuccess)
		{
			return Failed(set.Failure!);
		}

		var plan = set.Value.Plan(policy.Value, DateTimeOffset.UtcNow, backups.FreeSpace(server));
		var removed = new List<string>();

		if (mode == "apply")
		{
			var allowed = set.Value.CanApply(plan);
			if (!allowed.IsSuccess)
			{
				return Failed(allowed.Failure!);
			}

			var names = plan.Candidates.Select(candidate => candidate.Name).ToArray();
			var outcome = backups.Remove(server, names);
			if (!outcome.IsSuccess)
			{
				return Failed(outcome.Failure!);
			}

			removed.AddRange(outcome.Value.Select(name => name.Value));
		}

		return Reply(Result.Ok(new RetentionPayload
		{
			Mode = mode,
			Candidates = [.. plan.Candidates.Select(candidate => candidate.Name.Value)],
			Removed = removed,
			Bytes = plan.ReclaimableBytes,
			FreeBefore = plan.FreeBefore,
			FreeAfter = mode == "apply" ? plan.FreeAfter : plan.FreeBefore,
			TargetFree = plan.TargetFreeBytes,
			TargetMet = plan.TargetMet,
		}));
	}

	private static int Number(string? raw) =>
		int.TryParse(raw, System.Globalization.NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
			? value
			: -1;

	private async Task<Result<T>> WithStaged<T>(
		AgentRequest request,
		int index,
		Func<StagedFile, Task<Result<T>>> action)
	{
		var staged = StagedFile.Create(Argument(request, index));

		return staged.IsSuccess
			? await action(staged.Value).ConfigureAwait(false)
			: Result.Fail<T>(staged.Failure!);
	}

	private Task<bool> IsRunningAsync(RegisteredServer server, CancellationToken ct) =>
		service.IsActiveAsync(server, ct);

	private static string? Argument(AgentRequest request, int index) =>
		index < request.Args.Count ? request.Args[index] : null;

	/// <summary>
	/// Свободный текст для настроек: проверка длины и отсутствия управляющих символов.
	/// </summary>
	private static Result<string> Text(string? raw, int maximum, bool allowEmpty = false)
	{
		var value = (raw ?? string.Empty).Trim();

		if (value.Length > maximum || value.Any(char.IsControl) || (value.Length == 0 && !allowEmpty))
		{
			return Failure.Invalid("Недопустимое значение");
		}

		return value;
	}

	private static AgentReply Reply<T>(Result<T> result) =>
		result.IsSuccess
			? AgentReply.Success(JsonSerializer.SerializeToNode(result.Value, PanelJson.Options))
			: Failed(result.Failure!);

	private static AgentReply Reply<TSource, TPayload>(Result<TSource> result, Func<TSource, TPayload> map) =>
		Reply(result.Map(map));

	private static AgentReply Failed(Failure failure) => AgentReply.Failure(
		failure.Code switch
		{
			FailureCode.NotFound => "notfound",
			FailureCode.Invalid => "invalid",
			FailureCode.Conflict => "conflict",
			FailureCode.Forbidden => "forbidden",
			_ => "unavailable",
		},
		failure.Message);
}
