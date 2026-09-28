using System.Text.Json;
using Panel.Agent.Content;
using Panel.Contracts;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.Runtime.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Transport;

/// <summary>
/// Списки забаненных игроков и белого списка (интерфейс 2.1 §3). Только чтение: снять бан или
/// убрать из белого списка — это команда консоли, операция <c>player-action</c>.
/// </summary>
public sealed class PlayerListOperations : IServerOperations
{
	private readonly PlayerListsBackend lists;

	public PlayerListOperations(PlayerListsBackend lists)
	{
		ArgumentNullException.ThrowIfNull(lists);
		this.lists = lists;
	}

	bool IServerOperations.Serves(string op) => op == AgentProtocol.OpPlayerLists;

	public async Task<AgentReply> ExecuteAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(request);

		var result = await lists.ReadAsync(server, ct).ConfigureAwait(false);
		return result.Match(
			value => AgentReply.Success(JsonSerializer.SerializeToNode(ToPayload(value), PanelJson.Options)),
			Refuse);
	}

	internal static PlayerListsPayload ToPayload(PlayerLists lists) => new()
	{
		Banned = [.. lists.Banned.Select(ToPayload)],
		Whitelisted = [.. lists.Whitelisted.Select(ToPayload)],
	};

	private static PlayerListEntryPayload ToPayload(PlayerListEntry entry) => new()
	{
		Player = entry.PlayerName,
		Uid = entry.PlayerUid,
		IssuedBy = entry.IssuedBy,
		Until = entry.Forever ? null : entry.UntilSeconds,
		Forever = entry.Forever,
		Reason = entry.Reason,
	};

	private static AgentReply Refuse(Failure failure) => AgentReply.Failure(
		failure.Code switch
		{
			FailureCode.NotFound => "notfound",
			FailureCode.Invalid => "invalid",
			FailureCode.Conflict => "conflict",
			_ => "unavailable",
		},
		failure.Text);
}
