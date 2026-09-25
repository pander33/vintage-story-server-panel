using Panel.Contracts;
using Panel.Contracts.Agent;
using Panel.Provisioning.Domain;
using Panel.Provisioning.Ports;
using Panel.SharedKernel;

namespace Panel.Agent.Client;

/// <summary>Создание сервера и дела панели через протокол демона; все вызовы идут серверу «-».</summary>
public sealed class AgentProvisioningHost : IProvisioningHost
{
	private readonly AgentClient client;

	public AgentProvisioningHost(AgentClient client)
	{
		ArgumentNullException.ThrowIfNull(client);
		this.client = client;
	}

	public Task<Result<RuntimeProbePayload>> RuntimeProbeAsync(CancellationToken ct) =>
		client.CallAsync<RuntimeProbePayload>(AgentProtocol.OpRuntimeProbe, InstanceId.Registry, [], ct);

	public Task<Result<DiskFreePayload>> DiskFreeAsync(CancellationToken ct) =>
		client.CallAsync<DiskFreePayload>(AgentProtocol.OpDiskFree, InstanceId.Registry, [], ct);

	public Task<Result<InstanceCreatedPayload>> CreateInstanceAsync(ServerSpec spec, StagedFile package, CancellationToken ct) =>
		client.CallAsync<InstanceCreatedPayload>(
			AgentProtocol.OpInstanceCreate,
			InstanceId.Registry,
			[spec.ToJson(package).ToJsonString(PanelJson.Options)],
			ct);

	public Task<Result<ServiceInstalledPayload>> InstallServiceAsync(InstanceId instance, CancellationToken ct) =>
		client.CallAsync<ServiceInstalledPayload>(
			AgentProtocol.OpInstanceServiceInstall, InstanceId.Registry, [instance.Value], ct);

	public Task<Result<PanelEnvironmentSetPayload>> SetPanelEnvironmentAsync(PanelSetting setting, CancellationToken ct) =>
		client.CallAsync<PanelEnvironmentSetPayload>(
			AgentProtocol.OpPanelEnvSet, InstanceId.Registry, [setting.Key, setting.Value], ct);

	public Task<Result<PanelRestartPayload>> RestartPanelAsync(CancellationToken ct) =>
		client.CallAsync<PanelRestartPayload>(AgentProtocol.OpPanelRestart, InstanceId.Registry, [], ct);

	public Task<Result<InstancesRootSetPayload>> SetInstancesRootAsync(string path, CancellationToken ct) =>
		client.CallAsync<InstancesRootSetPayload>(AgentProtocol.OpInstancesRootSet, InstanceId.Registry, [path], ct);

	public Task<Result<PanelLogsPayload>> PanelLogsAsync(int lines, CancellationToken ct) =>
		client.CallAsync<PanelLogsPayload>(
			AgentProtocol.OpPanelLogs, InstanceId.Registry, [lines.ToString(System.Globalization.CultureInfo.InvariantCulture)], ct);
}
