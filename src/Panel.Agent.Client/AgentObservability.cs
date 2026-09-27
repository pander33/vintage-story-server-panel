using System.Globalization;
using System.Text.Json.Nodes;
using Panel.Contracts.Agent;
using Panel.Contracts.ServerCommands;
using Panel.Observability.Domain;
using Panel.Observability.Ports;
using Panel.Runtime.Domain;
using Panel.Runtime.Ports;
using Panel.SharedKernel;

namespace Panel.Agent.Client;

/// <summary>Лента игровых событий через протокол демона.</summary>
public sealed class AgentGameEventFeed : IGameEventFeed
{
	private readonly AgentClient client;

	public AgentGameEventFeed(AgentClient client)
	{
		ArgumentNullException.ThrowIfNull(client);
		this.client = client;
	}

	public async Task<Result<GameEventPage>> ReadAsync(InstanceId instance, int count, CancellationToken ct)
	{
		var page = await client
			.CallAsync<GameEventsPayload>(
				AgentProtocol.OpGameEvents,
				instance,
				[Math.Clamp(count, 1, 500).ToString(CultureInfo.InvariantCulture)],
				ct)
			.ConfigureAwait(false);

		return page.Map(value => new GameEventPage(
			[.. value.Events.Select(item => new GameEvent(
				item.At, item.Seq, item.Kind, item.Player, item.Uid, item.Channel, item.Text))],
			value.Supported));
	}
}

/// <summary>Действия над игроками через протокол демона.</summary>
public sealed class AgentPlayerCommands : IPlayerCommands
{
	private readonly AgentClient client;

	public AgentPlayerCommands(AgentClient client)
	{
		ArgumentNullException.ThrowIfNull(client);
		this.client = client;
	}

	public Task<Result<PlayerActionPayload>> SendAsync(InstanceId instance, PlayerCommand command, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(command);
		return client.CallAsync<PlayerActionPayload>(AgentProtocol.OpPlayerAction, instance, command.ToArguments(), ct);
	}
}

/// <summary>Списки забаненных игроков и белого списка через протокол демона (интерфейс 2.1 §3).</summary>
public sealed class AgentPlayerListStore : IPlayerListStore
{
	private readonly AgentClient client;

	public AgentPlayerListStore(AgentClient client)
	{
		ArgumentNullException.ThrowIfNull(client);
		this.client = client;
	}

	public async Task<Result<PlayerLists>> ReadAsync(InstanceId instance, CancellationToken ct)
	{
		var lists = await client
			.CallAsync<PlayerListsPayload>(AgentProtocol.OpPlayerLists, instance, [], ct)
			.ConfigureAwait(false);

		return lists.Map(value => new PlayerLists(
			[.. value.Banned.Select(ToEntry)],
			[.. value.Whitelisted.Select(ToEntry)]));
	}

	private static PlayerListEntry ToEntry(PlayerListEntryPayload item) =>
		new(item.Player, item.Uid, item.IssuedBy, item.Forever ? null : item.Until, item.Reason);
}

/// <summary>
/// Снимок телеметрии как есть. Отказ демона даёт недоступный снимок, и неудачный опрос тоже
/// попадает в историю.
/// </summary>
public sealed class AgentTelemetrySource : ITelemetrySource
{
	private readonly AgentClient client;

	public AgentTelemetrySource(AgentClient client)
	{
		ArgumentNullException.ThrowIfNull(client);
		this.client = client;
	}

	public async Task<JsonObject> ReadAsync(InstanceId instance, CancellationToken ct)
	{
		var snapshot = await client
			.CallAsync<JsonObject>(AgentProtocol.OpTelemetry, instance, [], ct)
			.ConfigureAwait(false);

		return snapshot.Match(
			value => value,
			_ => new JsonObject
			{
				["available"] = false,
				["stale"] = true,
				["detail"] = "Ошибка чтения телеметрии",
				["players"] = new JsonArray(),
			});
	}
}

/// <summary>Замер метрик через протокол демона.</summary>
public sealed class AgentMetricsSource : IMetricsSource
{
	private readonly AgentClient client;

	public AgentMetricsSource(AgentClient client)
	{
		ArgumentNullException.ThrowIfNull(client);
		this.client = client;
	}

	public Task<Result<JsonObject>> ReadAsync(InstanceId instance, CancellationToken ct) =>
		client.CallAsync<JsonObject>(AgentProtocol.OpMetrics, instance, [], ct);
}
