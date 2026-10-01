using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Panel.Observability.Domain;

/// <summary>Игрок в свежем снимке телеметрии.</summary>
public sealed record SeenPlayer(string Name, string? Uid);

/// <summary>Что панель помнит об игроке.</summary>
public sealed record PlayerRecord
{
	/// <summary>UID учётной записи игры, если мод его прислал; иначе пусто, и игрок узнаётся по имени.</summary>
	public required string Uid { get; init; }

	public required string Name { get; init; }
	public required long FirstSeen { get; init; }
	public required long LastSeen { get; init; }
	public required long PlaytimeSeconds { get; init; }
	public required int Sessions { get; init; }
}

/// <summary>
/// История игроков сервера: когда впервые и последний раз был в игре, сколько всего наиграл и
/// сколько раз заходил.
/// </summary>
/// <remarks>
/// <para>
/// Источник — снимки телеметрии, а не лента входов и выходов: снимок приходит раз в несколько
/// секунд и сам говорит, кто сейчас в игре, а пропущенное событие выхода оставило бы игрока
/// «в игре» навсегда. Время между двумя снимками, в которых игрок есть, прибавляется к его
/// времени в игре. Если перерыв дольше <see cref="Gap"/> (панель или сервер не работали), это
/// новый заход, и перерыв не засчитывается.
/// </para>
/// <para>
/// Адресов игроков здесь нет вовсе: история хранится дольше снимков, и копить адреса незачем.
/// UID наблюдателю не показывается, как и в остальной панели.
/// </para>
/// </remarks>
public sealed class PlayerRoster
{
	/// <summary>Сколько игроков помнится; при переполнении забываются те, кто дольше всех не заходил.</summary>
	public const int MaxPlayers = 5000;

	private readonly Dictionary<string, PlayerRecord> players = new(StringComparer.Ordinal);
	private HashSet<string> online = new(StringComparer.Ordinal);

	public PlayerRoster(TimeSpan gap)
	{
		Gap = gap;
	}

	/// <summary>Перерыв между снимками, после которого заход считается новым.</summary>
	public TimeSpan Gap { get; }

	/// <summary>Когда последний раз пришёл свежий снимок; до первого — ноль.</summary>
	public long ObservedAt { get; private set; }

	public int Count => players.Count;

	/// <summary>
	/// Свежий снимок: кто в игре сейчас. Возвращает истину, если состав игроков изменился, —
	/// тогда историю стоит записать сразу, а не по таймеру.
	/// </summary>
	public bool Observe(IReadOnlyList<SeenPlayer> current, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(current);

		var at = now.ToUnixTimeSeconds();
		var gap = (long) Gap.TotalSeconds;
		var seen = new HashSet<string>(StringComparer.Ordinal);

		foreach (var player in current)
		{
			if (string.IsNullOrWhiteSpace(player.Name))
			{
				continue;
			}

			var key = Key(player);
			if (!seen.Add(key))
			{
				continue;
			}

			if (players.TryGetValue(key, out var known))
			{
				var continues = online.Contains(key) && at - known.LastSeen is >= 0 and var elapsed && elapsed <= gap;
				players[key] = known with
				{
					Name = player.Name,
					LastSeen = Math.Max(known.LastSeen, at),
					PlaytimeSeconds = known.PlaytimeSeconds + (continues ? at - known.LastSeen : 0),
					Sessions = continues ? known.Sessions : known.Sessions + 1,
				};
			}
			else
			{
				players[key] = new PlayerRecord
				{
					Uid = player.Uid ?? string.Empty,
					Name = player.Name,
					FirstSeen = at,
					LastSeen = at,
					PlaytimeSeconds = 0,
					Sessions = 1,
				};
			}
		}

		// Снимок после долгого перерыва: прежние «в игре» уже не продолжают заход.
		var changed = !seen.SetEquals(online) || (ObservedAt > 0 && at - ObservedAt > gap);
		online = seen;
		ObservedAt = at;
		Trim();
		return changed;
	}

	/// <summary>
	/// Игроки, недавние первыми, с отметкой «в игре сейчас». Отметка держится, пока последний свежий
	/// снимок моложе <see cref="Gap"/>: устаревший или пропущенный снимок означает «неизвестно», а не
	/// «все вышли», и короткая заминка не дробит заход. После порога снимок уже ничего не говорит.
	/// </summary>
	public IReadOnlyList<(PlayerRecord Player, bool Online)> List(DateTimeOffset now)
	{
		var current = ObservedAt > 0 && now.ToUnixTimeSeconds() - ObservedAt <= (long) Gap.TotalSeconds;
		return
		[
			.. players
				.OrderByDescending(entry => entry.Value.LastSeen)
				.ThenBy(entry => entry.Value.Name, StringComparer.OrdinalIgnoreCase)
				.Select(entry => (entry.Value, current && online.Contains(entry.Key))),
		];
	}

	/// <summary>С какого момента панель ведёт историю; ноль, если ещё никого не видела.</summary>
	public long Since => players.Count == 0 ? 0 : players.Values.Min(player => player.FirstSeen);

	public JsonObject ToJson() => new()
	{
		["version"] = 1,
		["players"] = new JsonArray([.. players.Values.OrderBy(player => player.FirstSeen).Select(player => (JsonNode?) new JsonObject
		{
			["uid"] = player.Uid,
			["name"] = player.Name,
			["first_seen"] = player.FirstSeen,
			["last_seen"] = player.LastSeen,
			["playtime_seconds"] = player.PlaytimeSeconds,
			["sessions"] = player.Sessions,
		})]),
	};

	/// <summary>История из <c>players.json</c>; нечитаемые записи пропускаются.</summary>
	public static PlayerRoster FromJson(JsonObject? stored, TimeSpan gap)
	{
		var roster = new PlayerRoster(gap);
		foreach (var item in (stored?["players"] as JsonArray ?? []).OfType<JsonObject>())
		{
			var name = Text(item["name"]);
			if (name.Length == 0)
			{
				continue;
			}

			var record = new PlayerRecord
			{
				Uid = Text(item["uid"]),
				Name = name,
				FirstSeen = Long(item["first_seen"]),
				LastSeen = Long(item["last_seen"]),
				PlaytimeSeconds = Math.Max(0, Long(item["playtime_seconds"])),
				Sessions = (int) Math.Clamp(Long(item["sessions"]), 0, int.MaxValue),
			};
			roster.players[Key(new SeenPlayer(record.Name, record.Uid))] = record;
		}

		roster.Trim();
		return roster;
	}

	/// <summary>Разбор игроков снимка телеметрии: <c>players[].name</c> и <c>uid</c>.</summary>
	public static IReadOnlyList<SeenPlayer> FromSnapshot(JsonObject snapshot)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		return
		[
			.. (snapshot["players"] as JsonArray ?? []).OfType<JsonObject>()
				.Select(player => new SeenPlayer(Text(player["name"]), Text(player["uid"]) is { Length: > 0 } uid ? uid : null))
				.Where(player => player.Name.Length > 0),
		];
	}

	private static string Key(SeenPlayer player) =>
		player.Uid is { Length: > 0 } uid ? "uid:" + uid : "name:" + player.Name.ToLowerInvariant();

	private void Trim()
	{
		if (players.Count <= MaxPlayers)
		{
			return;
		}

		foreach (var key in players.OrderBy(entry => entry.Value.LastSeen).Take(players.Count - MaxPlayers).Select(entry => entry.Key).ToList())
		{
			players.Remove(key);
		}
	}

	private static string Text(JsonNode? node) =>
		node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>().Trim() : string.Empty;

	private static long Long(JsonNode? node) =>
		node is JsonValue value && long.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
			? number
			: 0;
}
