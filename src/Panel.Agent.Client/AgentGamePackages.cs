using Panel.Contracts.Agent;
using Panel.Maintenance.Domain;
using Panel.Maintenance.Ports;
using Panel.SharedKernel;

namespace Panel.Agent.Client;

/// <summary>Программа сервера через протокол демона.</summary>
public sealed class AgentGamePackages : IGamePackages
{
	private readonly AgentClient client;

	public AgentGamePackages(AgentClient client)
	{
		ArgumentNullException.ThrowIfNull(client);
		this.client = client;
	}

	public Task<Result<GamePackageInspectionPayload>> InspectAsync(InstanceId instance, StagedFile package, CancellationToken ct) =>
		client.CallAsync<GamePackageInspectionPayload>(AgentProtocol.OpGamePackageInspect, instance, [package.Name], ct);

	public Task<Result<GameUpdateAppliedPayload>> ApplyAsync(InstanceId instance, StagedFile package, CancellationToken ct) =>
		client.CallAsync<GameUpdateAppliedPayload>(AgentProtocol.OpGameUpdateApply, instance, [package.Name], ct);

	public Task<Result<GameRollbackPayload>> RollbackAsync(InstanceId instance, SnapshotName snapshot, CancellationToken ct) =>
		client.CallAsync<GameRollbackPayload>(AgentProtocol.OpGameUpdateRollback, instance, [snapshot.Value], ct);

	public Task<Result<GameVersionRecordedPayload>> RecordVersionAsync(InstanceId instance, string version, CancellationToken ct) =>
		client.CallAsync<GameVersionRecordedPayload>(AgentProtocol.OpPanelGameVersion, instance, [version], ct);

	public Task<Result<ProgramSnapshotsPayload>> HistoryAsync(InstanceId instance, CancellationToken ct) =>
		client.CallAsync<ProgramSnapshotsPayload>(AgentProtocol.OpGameHistory, instance, [], ct);
}
