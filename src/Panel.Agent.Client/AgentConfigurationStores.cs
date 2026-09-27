using System.Text.Json.Nodes;
using Panel.Configuration.Domain;
using Panel.Configuration.Ports;
using Panel.Contracts.Agent;
using Panel.Contracts.Configuration;
using Panel.Contracts.ServerCommands;
using Panel.Runtime.Domain;
using Panel.Runtime.Ports;
using Panel.SharedKernel;

namespace Panel.Agent.Client;

/// <summary><c>serverconfig.json</c> через протокол демона.</summary>
public sealed class AgentServerConfigStore : IServerConfigStore
{
	private readonly AgentClient client;

	public AgentServerConfigStore(AgentClient client)
	{
		ArgumentNullException.ThrowIfNull(client);
		this.client = client;
	}

	public Task<Result<ServerConfigFilePayload>> ReadAsync(InstanceId instance, CancellationToken ct) =>
		client.CallAsync<ServerConfigFilePayload>(AgentProtocol.OpConfigGet, instance, [], ct);

	public Task<Result<ConfigWritePayload>> WriteAsync(InstanceId instance, StagedFile staged, CancellationToken ct) =>
		client.CallAsync<ConfigWritePayload>(AgentProtocol.OpConfigPut, instance, [staged.Name], ct);

	public Task<Result<ConfigBackupsPayload>> ListBackupsAsync(InstanceId instance, CancellationToken ct) =>
		client.CallAsync<ConfigBackupsPayload>(AgentProtocol.OpConfigBackups, instance, [], ct);

	public Task<Result<ConfigRollbackPayload>> RollbackAsync(
		InstanceId instance,
		ConfigBackupName backup,
		CancellationToken ct) =>
		client.CallAsync<ConfigRollbackPayload>(AgentProtocol.OpConfigRollback, instance, [backup.Value], ct);
}

/// <summary>Схема параметров мира через протокол демона.</summary>
public sealed class AgentWorldSchemaStore : IWorldSchemaStore
{
	private readonly AgentClient client;

	public AgentWorldSchemaStore(AgentClient client)
	{
		ArgumentNullException.ThrowIfNull(client);
		this.client = client;
	}

	public Task<Result<WorldSchemaPayload>> ReadAsync(InstanceId instance, CancellationToken ct) =>
		client.CallAsync<WorldSchemaPayload>(AgentProtocol.OpWorldConfigSchema, instance, [], ct);
}

/// <summary>
/// Живая конфигурация мира: читается из телеметрии, меняется отдельной операцией демона,
/// а не консольным действием из каталога.
/// </summary>
public sealed class AgentLiveWorldConfig : ILiveWorldConfig
{
	private readonly AgentClient client;

	public AgentLiveWorldConfig(AgentClient client)
	{
		ArgumentNullException.ThrowIfNull(client);
		this.client = client;
	}

	public async Task<LiveWorldConfig> ReadAsync(InstanceId instance, CancellationToken ct)
	{
		var telemetry = await client
			.CallAsync<TelemetryPayload>(AgentProtocol.OpTelemetry, instance, [], ct)
			.ConfigureAwait(false);

		return telemetry.Match(
			payload => new LiveWorldConfig(
				payload.Available,
				payload.World?["configuration"] is JsonObject configuration
					? (JsonObject) configuration.DeepClone()
					: [],
				payload.Detail ?? string.Empty),
			failure => new LiveWorldConfig(false, [], failure.Message));
	}

	public async Task<Result<Done>> ChangeAsync(InstanceId instance, WorldConfigCommand command, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(command);

		var sent = await client
			.CallAsync<JsonObject>(AgentProtocol.OpWorldConfigAction, instance, command.ToArguments(), ct)
			.ConfigureAwait(false);

		return sent.Map(_ => Done.Value);
	}
}
