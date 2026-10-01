using System.Text.Json.Nodes;
using Panel.Observability.Domain;

namespace Panel.Observability.Tests;

/// <summary>
/// История игроков по снимкам телеметрии: время в игре копится между соседними снимками, а
/// перерыв дольше порога начинает новый заход.
/// </summary>
public sealed class PlayerRosterTests
{
	private static readonly TimeSpan gap = TimeSpan.FromSeconds(90);
	private static readonly DateTimeOffset start = new(2026, 10, 1, 18, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Время_в_игре_копится_между_снимками()
	{
		var roster = new PlayerRoster(gap);

		Assert.True(roster.Observe([new SeenPlayer("alice", "u1")], start));
		Assert.False(roster.Observe([new SeenPlayer("alice", "u1")], start.AddSeconds(60)));
		roster.Observe([new SeenPlayer("alice", "u1")], start.AddSeconds(120));

		var (player, online) = Assert.Single(Current(roster));
		Assert.True(online);
		Assert.Equal(120, player.PlaytimeSeconds);
		Assert.Equal(1, player.Sessions);
		Assert.Equal(start.ToUnixTimeSeconds(), player.FirstSeen);
		Assert.Equal(start.AddSeconds(120).ToUnixTimeSeconds(), player.LastSeen);
	}

	[Fact]
	public void Перерыв_дольше_порога_начинает_новый_заход_и_не_засчитывается()
	{
		var roster = new PlayerRoster(gap);
		roster.Observe([new SeenPlayer("alice", "u1")], start);
		roster.Observe([new SeenPlayer("alice", "u1")], start.AddSeconds(60));

		// Панель не работала десять минут.
		roster.Observe([new SeenPlayer("alice", "u1")], start.AddMinutes(11));

		var player = Current(roster)[0].Player;
		Assert.Equal(60, player.PlaytimeSeconds);
		Assert.Equal(2, player.Sessions);
	}

	[Fact]
	public void Выход_и_повторный_вход_это_два_захода()
	{
		var roster = new PlayerRoster(gap);
		roster.Observe([new SeenPlayer("alice", "u1")], start);
		roster.Observe([], start.AddSeconds(30));
		roster.Observe([new SeenPlayer("alice", "u1")], start.AddSeconds(60));

		var (player, online) = Current(roster)[0];
		Assert.Equal(0, player.PlaytimeSeconds);
		Assert.Equal(2, player.Sessions);
		Assert.True(online);
	}

	/// <summary>
	/// Устаревшие снимки (игра сохраняет мир, демон перезапускается) в учёт не попадают, и
	/// заминка короче порога заход не дробит.
	/// </summary>
	[Fact]
	public void Короткая_заминка_телеметрии_не_дробит_заход()
	{
		var roster = new PlayerRoster(gap);
		roster.Observe([new SeenPlayer("alice", "u1")], start);
		roster.Observe([new SeenPlayer("alice", "u1")], start.AddSeconds(60));

		var (player, online) = Current(roster)[0];
		Assert.Equal(1, player.Sessions);
		Assert.Equal(60, player.PlaytimeSeconds);
		Assert.True(online);
	}

	[Fact]
	public void Без_свежих_снимков_дольше_порога_игрок_не_в_игре()
	{
		var roster = new PlayerRoster(gap);
		roster.Observe([new SeenPlayer("alice", "u1")], start);

		Assert.True(roster.List(start.AddSeconds(90))[0].Online);
		Assert.False(roster.List(start.AddSeconds(91))[0].Online);
	}

	[Fact]
	public void Игрок_узнаётся_по_uid_и_после_смены_имени()
	{
		var roster = new PlayerRoster(gap);
		roster.Observe([new SeenPlayer("alice", "u1")], start);
		roster.Observe([new SeenPlayer("Alice2", "u1")], start.AddSeconds(30));

		var player = Assert.Single(Current(roster)).Player;
		Assert.Equal("Alice2", player.Name);
		Assert.Equal(30, player.PlaytimeSeconds);
	}

	[Fact]
	public void Без_uid_игрок_узнаётся_по_имени_без_учёта_регистра()
	{
		var roster = new PlayerRoster(gap);
		roster.Observe([new SeenPlayer("Bob", null)], start);
		roster.Observe([new SeenPlayer("bob", null)], start.AddSeconds(30));

		Assert.Single(Current(roster));
	}

	[Fact]
	public void Недавние_игроки_первыми_а_ушедший_не_в_игре()
	{
		var roster = new PlayerRoster(gap);
		roster.Observe([new SeenPlayer("alice", "u1")], start);
		roster.Observe([new SeenPlayer("bob", "u2")], start.AddSeconds(30));

		var list = Current(roster);
		Assert.Equal(["bob", "alice"], list.Select(entry => entry.Player.Name));
		Assert.Equal([true, false], list.Select(entry => entry.Online));
		Assert.Equal(start.ToUnixTimeSeconds(), roster.Since);
	}

	[Fact]
	public void История_переживает_файл_но_заход_после_перезапуска_новый()
	{
		var roster = new PlayerRoster(gap);
		roster.Observe([new SeenPlayer("alice", "u1")], start);
		roster.Observe([new SeenPlayer("alice", "u1")], start.AddSeconds(60));

		var restored = PlayerRoster.FromJson(JsonNode.Parse(roster.ToJson().ToJsonString())!.AsObject(), gap);
		Assert.Equal(Current(roster)[0].Player, Current(restored)[0].Player);
		Assert.False(Current(restored)[0].Online);

		restored.Observe([new SeenPlayer("alice", "u1")], start.AddSeconds(70));
		Assert.Equal(2, Current(restored)[0].Player.Sessions);
		Assert.Equal(60, Current(restored)[0].Player.PlaytimeSeconds);
	}

	[Fact]
	public void Снимок_разбирается_без_адресов_и_пустых_имён()
	{
		var snapshot = new JsonObject
		{
			["players"] = new JsonArray(
				new JsonObject { ["name"] = "alice", ["uid"] = "u1", ["ip"] = "10.0.0.5" },
				new JsonObject { ["name"] = "", ["uid"] = "u2" },
				new JsonObject { ["name"] = "bob" }),
		};

		var players = PlayerRoster.FromSnapshot(snapshot);

		Assert.Equal([new SeenPlayer("alice", "u1"), new SeenPlayer("bob", null)], players);
		Assert.DoesNotContain("10.0.0.5", new PlayerRoster(gap).ToJson().ToJsonString(), StringComparison.Ordinal);
	}

	[Fact]
	public void Переполнение_забывает_тех_кто_дольше_всех_не_заходил()
	{
		var roster = new PlayerRoster(gap);
		for (var index = 0; index <= PlayerRoster.MaxPlayers; index++)
		{
			roster.Observe([new SeenPlayer($"p{index}", $"u{index}")], start.AddSeconds(index));
		}

		Assert.Equal(PlayerRoster.MaxPlayers, roster.Count);
		Assert.DoesNotContain(Current(roster), entry => entry.Player.Name == "p0");
	}

	/// <summary>Список на момент последнего снимка; до первого снимка никто не в игре.</summary>
	private static IReadOnlyList<(PlayerRecord Player, bool Online)> Current(PlayerRoster roster) =>
		roster.List(DateTimeOffset.FromUnixTimeSeconds(roster.ObservedAt));
}
