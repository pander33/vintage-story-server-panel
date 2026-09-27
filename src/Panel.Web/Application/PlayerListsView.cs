using System.Text.Json.Nodes;
using Panel.Contracts.Http;
using Panel.Runtime.Domain;

namespace Panel.Web.Application;

/// <summary>
/// Ответ <c>GET /api/players/lists</c> (интерфейс 2.1 §3): списки из файлов игры, режим белого
/// списка из <c>serverconfig.json</c> и приватность UID по праву <c>SeePlayerAddresses</c>.
/// </summary>
/// <remarks>
/// Два контекста сразу (Runtime и Configuration), поэтому сборка ответа живёт в композиционном
/// корне. UID скрывает панель, а не демон: демон не знает, кто спрашивает.
/// </remarks>
public static class PlayerListsView
{
	public static PlayerListsResponse Compose(PlayerLists lists, JsonObject? config, bool hideUid)
	{
		ArgumentNullException.ThrowIfNull(lists);

		return new PlayerListsResponse
		{
			Banned = [.. lists.Banned.Select(entry => Entry(entry, hideUid))],
			Whitelisted = [.. lists.Whitelisted.Select(entry => Entry(entry, hideUid))],
			WhitelistMode = config is not null && WhitelistOnly(config),
			Stale = config is null,
		};
	}

	/// <summary>
	/// Пускает ли сервер только игроков из белого списка. Старые версии игры пишут флаг
	/// <c>OnlyWhitelisted</c>, новые — <c>WhitelistMode</c> (число или имя значения; «включён» —
	/// 2 или <c>On</c>). Точные значения требуют сверки с настоящим сервером (открытый вопрос плана).
	/// </summary>
	public static bool WhitelistOnly(JsonObject config)
	{
		ArgumentNullException.ThrowIfNull(config);

		if (config["WhitelistMode"] is JsonValue mode)
		{
			if (mode.TryGetValue<int>(out var number))
			{
				return number == 2;
			}

			if (mode.TryGetValue<string>(out var name))
			{
				return string.Equals(name, "On", StringComparison.OrdinalIgnoreCase);
			}
		}

		return config["OnlyWhitelisted"] is JsonValue flag && flag.TryGetValue<bool>(out var only) && only;
	}

	private static PlayerListEntryResponse Entry(PlayerListEntry entry, bool hideUid) => new()
	{
		Player = entry.PlayerName,
		Uid = hideUid ? string.Empty : entry.PlayerUid ?? string.Empty,
		IssuedBy = entry.IssuedBy,
		Until = entry.Forever ? null : entry.UntilSeconds,
		Forever = entry.Forever,
		Reason = entry.Reason,
	};
}
