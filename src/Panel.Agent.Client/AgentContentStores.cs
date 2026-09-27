using Panel.Content.Domain;
using Panel.Content.Ports;
using Panel.Contracts.Agent;
using Panel.Contracts.Content;
using Panel.SharedKernel;

namespace Panel.Agent.Client;

/// <summary>
/// Моды через протокол демона.
/// </summary>
/// <remarks>
/// В адаптерах нет проверок: аргументы уже доменные типы, ответ уже в published language.
/// Условие в адаптере означало бы, что сюда попало правило предметной области.
/// </remarks>
public sealed class AgentModStore : IModStore
{
	private readonly AgentClient client;

	public AgentModStore(AgentClient client)
	{
		ArgumentNullException.ThrowIfNull(client);
		this.client = client;
	}

	public Task<Result<ModsPayload>> ListAsync(InstanceId instance, CancellationToken cancellationToken) =>
		client.CallAsync<ModsPayload>(AgentProtocol.OpMods, instance, [], cancellationToken);

	public Task<Result<ModChangePayload>> InstallAsync(
		InstanceId instance,
		StagedFile staged,
		CancellationToken cancellationToken) =>
		client.CallAsync<ModChangePayload>(
			AgentProtocol.OpModInstall, instance, [staged.Name], cancellationToken);

	public Task<Result<ModChangePayload>> ToggleAsync(
		InstanceId instance,
		ModArchiveName mod,
		CancellationToken cancellationToken) =>
		client.CallAsync<ModChangePayload>(AgentProtocol.OpModToggle, instance, [mod.Value], cancellationToken);

	public Task<Result<ModChangePayload>> DeleteAsync(
		InstanceId instance,
		ModArchiveName mod,
		CancellationToken cancellationToken) =>
		client.CallAsync<ModChangePayload>(AgentProtocol.OpModDelete, instance, [mod.Value], cancellationToken);

	public Task<Result<ModChangePayload>> RollbackAsync(
		InstanceId instance,
		ModId mod,
		ModArchiveName archived,
		CancellationToken cancellationToken) =>
		client.CallAsync<ModChangePayload>(
			AgentProtocol.OpModRollback, instance, [mod.Value, archived.Value], cancellationToken);

	public Task<Result<ModProfilePayload>> SaveProfileAsync(
		InstanceId instance,
		ModProfileName profile,
		CancellationToken cancellationToken) =>
		client.CallAsync<ModProfilePayload>(
			AgentProtocol.OpModProfileSave, instance, [profile.Value], cancellationToken);

	public Task<Result<ModProfilePayload>> ApplyProfileAsync(
		InstanceId instance,
		ModProfileName profile,
		CancellationToken cancellationToken) =>
		client.CallAsync<ModProfilePayload>(
			AgentProtocol.OpModProfileApply, instance, [profile.Value], cancellationToken);

	public async Task<Result<Done>> DeleteProfileAsync(
		InstanceId instance,
		ModProfileName profile,
		CancellationToken cancellationToken)
	{
		var reply = await client
			.CallAsync<AcknowledgedPayload>(AgentProtocol.OpModProfileDelete, instance, [profile.Value], cancellationToken)
			.ConfigureAwait(false);

		return reply.Map(_ => Done.Value);
	}

	public Task<Result<ModConfigsPayload>> ListConfigsAsync(
		InstanceId instance,
		CancellationToken cancellationToken) =>
		client.CallAsync<ModConfigsPayload>(AgentProtocol.OpModConfigs, instance, [], cancellationToken);

	public Task<Result<ModConfigPayload>> ReadConfigAsync(
		InstanceId instance,
		ModConfigName name,
		CancellationToken cancellationToken) =>
		client.CallAsync<ModConfigPayload>(
			AgentProtocol.OpModConfigGet, instance, [name.Value], cancellationToken);

	public async Task<Result<Done>> WriteConfigAsync(
		InstanceId instance,
		ModConfigName name,
		StagedFile staged,
		CancellationToken cancellationToken)
	{
		var reply = await client
			.CallAsync<AcknowledgedPayload>(
				AgentProtocol.OpModConfigPut, instance, [name.Value, staged.Name], cancellationToken)
			.ConfigureAwait(false);

		return reply.Map(_ => Done.Value);
	}
}

/// <summary>Миры через протокол демона.</summary>
public sealed class AgentWorldStore : IWorldStore
{
	private readonly AgentClient client;

	public AgentWorldStore(AgentClient client)
	{
		ArgumentNullException.ThrowIfNull(client);
		this.client = client;
	}

	public Task<Result<WorldsPayload>> ListAsync(InstanceId instance, CancellationToken cancellationToken) =>
		client.CallAsync<WorldsPayload>(AgentProtocol.OpWorlds, instance, [], cancellationToken);

	public Task<Result<WorldChangePayload>> ImportAsync(
		InstanceId instance,
		StagedFile staged,
		WorldFileName name,
		CancellationToken cancellationToken) =>
		client.CallAsync<WorldChangePayload>(
			AgentProtocol.OpWorldImport, instance, [staged.Name, name.Value], cancellationToken);

	public Task<Result<WorldExportPayload>> ExportAsync(
		InstanceId instance,
		WorldFileName name,
		StagedFile staged,
		CancellationToken cancellationToken) =>
		client.CallAsync<WorldExportPayload>(
			AgentProtocol.OpWorldExport, instance, [name.Value, staged.Name], cancellationToken);

	public Task<Result<WorldChangePayload>> CloneAsync(
		InstanceId instance,
		WorldFileName source,
		WorldFileName target,
		CancellationToken cancellationToken) =>
		client.CallAsync<WorldChangePayload>(
			AgentProtocol.OpWorldClone, instance, [source.Value, target.Value], cancellationToken);

	public Task<Result<WorldChangePayload>> ActivateAsync(
		InstanceId instance,
		WorldFileName name,
		CancellationToken cancellationToken) =>
		client.CallAsync<WorldChangePayload>(
			AgentProtocol.OpWorldActivate, instance, [name.Value], cancellationToken);

	public Task<Result<WorldChangePayload>> CreateAsync(
		InstanceId instance,
		WorldPlan plan,
		CancellationToken cancellationToken) =>
		client.CallAsync<WorldChangePayload>(
			AgentProtocol.OpWorldCreate,
			instance,
			[plan.File.Value, plan.DisplayName, plan.Seed, PlayStyleWire.ToWire(plan.Style)],
			cancellationToken);

	public Task<Result<WorldChangePayload>> PutAwayAsync(
		InstanceId instance,
		WorldFileName name,
		WorldDisposition disposition,
		CancellationToken cancellationToken) =>
		client.CallAsync<WorldChangePayload>(
			AgentProtocol.OpWorldPutAway,
			instance,
			[name.Value, WorldDispositionWire.ToWire(disposition)],
			cancellationToken);

	public async Task<Result<IReadOnlyList<StoredWorldPayload>>> ListStoredAsync(
		InstanceId instance,
		CancellationToken cancellationToken)
	{
		var reply = await client
			.CallAsync<StoredWorldPayload[]>(AgentProtocol.OpWorldStored, instance, [], cancellationToken)
			.ConfigureAwait(false);

		return reply.Map(IReadOnlyList<StoredWorldPayload> (worlds) => worlds);
	}

	public Task<Result<WorldChangePayload>> RestoreStoredAsync(
		InstanceId instance,
		WorldDisposition disposition,
		StoredWorldName stored,
		WorldFileName target,
		CancellationToken cancellationToken) =>
		client.CallAsync<WorldChangePayload>(
			AgentProtocol.OpWorldRestore,
			instance,
			[WorldDispositionWire.ToWire(disposition), stored.Value, target.Value],
			cancellationToken);

	public Task<Result<WorldChangePayload>> DeleteStoredAsync(
		InstanceId instance,
		WorldDisposition disposition,
		StoredWorldName stored,
		CancellationToken cancellationToken) =>
		client.CallAsync<WorldChangePayload>(
			AgentProtocol.OpWorldStoredDelete,
			instance,
			[WorldDispositionWire.ToWire(disposition), stored.Value],
			cancellationToken);
}

/// <summary>Ход долгой операции через протокол демона.</summary>
public sealed class AgentOperationProgress : IOperationProgress
{
	private readonly AgentClient client;

	public AgentOperationProgress(AgentClient client)
	{
		ArgumentNullException.ThrowIfNull(client);
		this.client = client;
	}

	public Task<Result<OperationPayload>> CurrentAsync(InstanceId instance, CancellationToken cancellationToken) =>
		client.CallAsync<OperationPayload>(AgentProtocol.OpOperation, instance, [], cancellationToken);
}

/// <summary>Резервные копии через протокол демона.</summary>
public sealed class AgentBackupStore : IBackupStore
{
	private readonly AgentClient client;

	public AgentBackupStore(AgentClient client)
	{
		ArgumentNullException.ThrowIfNull(client);
		this.client = client;
	}

	public Task<Result<WorldExportPayload>> ExportPackageAsync(
		InstanceId instance,
		StagedFile staged,
		CancellationToken cancellationToken) =>
		client.CallAsync<WorldExportPayload>(AgentProtocol.OpTransferExport, instance, [staged.Name], cancellationToken);

	public Task<Result<BackupsPayload>> ListAsync(InstanceId instance, CancellationToken cancellationToken) =>
		client.CallAsync<BackupsPayload>(AgentProtocol.OpBackups, instance, [], cancellationToken);

	public Task<Result<BackupChangePayload>> CreateAsync(
		InstanceId instance,
		bool offline,
		CancellationToken cancellationToken) =>
		client.CallAsync<BackupChangePayload>(
			AgentProtocol.OpBackupCreate, instance, [offline ? "offline" : "online"], cancellationToken);

	public Task<Result<BackupContentsPayload>> InspectAsync(
		InstanceId instance,
		BackupName name,
		CancellationToken cancellationToken) =>
		client.CallAsync<BackupContentsPayload>(
			AgentProtocol.OpBackupInspect, instance, [name.Value], cancellationToken);

	public Task<Result<BackupChangePayload>> RestoreAsync(
		InstanceId instance,
		BackupName name,
		CancellationToken cancellationToken) =>
		client.CallAsync<BackupChangePayload>(
			AgentProtocol.OpBackupRestore, instance, [name.Value], cancellationToken);

	public Task<Result<BackupChangePayload>> DeleteAsync(
		InstanceId instance,
		BackupName name,
		CancellationToken cancellationToken) =>
		client.CallAsync<BackupChangePayload>(
			AgentProtocol.OpBackupDelete, instance, [name.Value], cancellationToken);

	public Task<Result<WorldChangePayload>> RestoreWorldAsync(
		InstanceId instance,
		BackupName backup,
		string world,
		WorldFileName target,
		CancellationToken cancellationToken) =>
		client.CallAsync<WorldChangePayload>(
			AgentProtocol.OpBackupRestoreWorld,
			instance,
			[backup.Value, world, target.Value],
			cancellationToken);

	public Task<Result<RetentionPayload>> ApplyRetentionAsync(
		InstanceId instance,
		RetentionPolicy policy,
		bool apply,
		CancellationToken cancellationToken) =>
		client.CallAsync<RetentionPayload>(
			AgentProtocol.OpBackupRetention,
			instance,
			[
				policy.KeepCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
				policy.KeepDays.ToString(System.Globalization.CultureInfo.InvariantCulture),
				policy.MinimumFreeGigabytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
				apply ? "apply" : "preview",
			],
			cancellationToken);
}

/// <summary>История через протокол демона.</summary>
public sealed class AgentHistoryStore : IHistoryStore
{
	private readonly AgentClient client;

	public AgentHistoryStore(AgentClient client)
	{
		ArgumentNullException.ThrowIfNull(client);
		this.client = client;
	}

	public Task<Result<HistoryPayload>> ListAsync(InstanceId instance, CancellationToken ct) =>
		client.CallAsync<HistoryPayload>(AgentProtocol.OpHistory, instance, [], ct);

	public Task<Result<HistoryDeletePayload>> DeleteAsync(
		InstanceId instance,
		HistoryStore store,
		HistoryKey key,
		CancellationToken ct) =>
		client.CallAsync<HistoryDeletePayload>(
			AgentProtocol.OpHistoryDelete, instance, [HistoryStoreWire.ToWire(store), key.Value], ct);

	public Task<Result<HistoryPrunePayload>> PruneAsync(
		InstanceId instance,
		HistoryStore store,
		HistoryKeep keep,
		CancellationToken ct) =>
		client.CallAsync<HistoryPrunePayload>(
			AgentProtocol.OpHistoryPrune,
			instance,
			[HistoryStoreWire.ToWire(store), keep.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)],
			ct);
}
