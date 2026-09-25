using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.SharedKernel;

namespace Panel.Agent.Client;

/// <summary>Реестр через протокол демона; все вызовы идут серверу «-».</summary>
public sealed class AgentRegistryAdministration : IRegistryAdministration
{
	private readonly AgentClient client;

	public AgentRegistryAdministration(AgentClient client)
	{
		ArgumentNullException.ThrowIfNull(client);
		this.client = client;
	}

	public Task<Result<RegistryListingPayload>> ListAsync(CancellationToken ct) =>
		client.CallAsync<RegistryListingPayload>(AgentProtocol.OpInstances, InstanceId.Registry, [], ct);

	public Task<Result<InstanceRenamedPayload>> RenameAsync(InstanceId instance, InstanceName name, CancellationToken ct) =>
		client.CallAsync<InstanceRenamedPayload>(
			AgentProtocol.OpInstanceRename, InstanceId.Registry, [instance.Value, name.Value], ct);

	public Task<Result<InstanceForgottenPayload>> ForgetAsync(InstanceId instance, CancellationToken ct) =>
		client.CallAsync<InstanceForgottenPayload>(
			AgentProtocol.OpInstanceForget, InstanceId.Registry, [instance.Value], ct);

	public Task<Result<InstanceDeletedPayload>> DeleteAsync(InstanceId instance, CancellationToken ct) =>
		client.CallAsync<InstanceDeletedPayload>(
			AgentProtocol.OpInstanceDelete, InstanceId.Registry, [instance.Value], ct);
}
