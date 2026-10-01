using System.Text.Json.Nodes;
using Panel.Observability.Domain;

namespace Panel.Observability.Tests;

/// <summary>История телеметрии без ввода-вывода.</summary>
public sealed class TelemetryHistoryTests
{
	private static readonly DateTimeOffset start = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

	[Theory]
	[InlineData(null, 0, "unavailable")]
	[InlineData(40.0, 10, "excellent")]
	[InlineData(40.0, 30, "good")]
	[InlineData(90.0, 30, "good")]
	[InlineData(200.0, 90, "unstable")]
	[InlineData(300.0, 10, "poor")]
	public void Качество_соединения(double? ping, double jitter, string expected) =>
		Assert.Equal(expected, ConnectionQuality.Of(ping, jitter));

	[Fact]
	public void Запись_проставляет_время_и_отбрасывает_устаревшее()
	{
		var history = new TelemetryHistory(TimeSpan.FromHours(1));

		history.Record(Snapshot(("Alice", 40)), start);
		var stored = history.Record(Snapshot(("Alice", 40)), start.AddHours(2));

		Assert.Equal(start.AddHours(2).ToUnixTimeSeconds(), stored["sampled_at"]!.GetValue<long>());
		Assert.Equal(1, history.Count);
	}

	[Fact]
	public void Игроки_с_оценкой_за_пять_минут()
	{
		var history = new TelemetryHistory(TimeSpan.FromHours(24));
		history.Record(Snapshot(("alice", 40)), start);
		history.Record(Snapshot(("alice", 60)), start.AddSeconds(2));
		history.Record(Snapshot(("alice", 300), ("Bob", null)), start.AddSeconds(4));

		var players = history.Players(start.AddSeconds(4));

		Assert.Equal(["alice", "Bob"], players.Select(player => player["name"]!.GetValue<string>()));

		var alice = players[0];
		Assert.Equal(300, alice["ping_ms"]!.GetValue<double>());
		Assert.Equal(133.3, alice["average_ping_ms"]!.GetValue<double>());
		Assert.Equal(300, alice["max_ping_ms"]!.GetValue<double>());
		Assert.Equal(130, alice["jitter_ms"]!.GetValue<double>());
		Assert.Equal(1, alice["spike_count"]!.GetValue<int>());
		Assert.Equal("poor", alice["quality"]!.GetValue<string>());

		// Без пинга качество неизвестно, средние пусты.
		Assert.Equal("unavailable", players[1]["quality"]!.GetValue<string>());
		Assert.Null(players[1]["average_ping_ms"]);
	}

	/// <summary>По устаревшему снимку игроки неизвестны, поэтому список пуст.</summary>
	[Fact]
	public void Устаревший_снимок_игроков_не_показывает()
	{
		var history = new TelemetryHistory(TimeSpan.FromHours(24));
		var stale = Snapshot(("Alice", 40));
		stale["stale"] = true;
		history.Record(stale, start);

		Assert.Empty(history.Players(start));
		Assert.Empty(history.Current(start)["players"]!.AsArray());
	}

	[Fact]
	public void Без_снимков_текущее_недоступно()
	{
		var current = new TelemetryHistory(TimeSpan.FromHours(1)).Current(start);

		Assert.False(current["available"]!.GetValue<bool>());
		Assert.True(current["stale"]!.GetValue<bool>());
	}

	[Fact]
	public void История_игрока_без_учёта_регистра()
	{
		var history = new TelemetryHistory(TimeSpan.FromHours(24));
		history.Record(Snapshot(("Alice", 40)), start);
		history.Record(Snapshot(("Alice", 50)), start.AddSeconds(2));

		var samples = history.PlayerHistory(TelemetryPlayer.Create("ALICE").Value, start.AddSeconds(2), TimeSpan.FromHours(1));

		Assert.Equal([40d, 50d], samples.Select(sample => sample.PingMilliseconds));
	}

	[Fact]
	public void Длинное_имя_игрока_отвергается() =>
		Assert.Equal("Имя игрока слишком длинное", TelemetryPlayer.Create(new string('a', 33)).Failure!.Message);

	/// <summary>Снимок, прочитанный дважды, учитывается один раз по <c>generated_at</c>.</summary>
	[Fact]
	public void Производительность_без_повторов_снимка()
	{
		var history = new TelemetryHistory(TimeSpan.FromHours(24));
		var snapshot = Snapshot(("Alice", 40));
		snapshot["generated_at"] = 1000;
		snapshot["observed_tps"] = 29.456;
		snapshot["long_tick_threshold_ms"] = 250;
		snapshot["long_ticks"] = new JsonArray(new JsonObject { ["at"] = 0, ["gap_ms"] = 301.26 });

		history.Record(snapshot, start);
		history.Record(snapshot, start.AddSeconds(2));

		var report = history.Performance(start.AddSeconds(2), TimeSpan.FromHours(1));

		var point = Assert.Single(report.Series);
		Assert.Equal(29.46, point.Tps);
		var tick = Assert.Single(report.LongTicks);
		Assert.Equal(start.ToUnixTimeSeconds(), tick.At);
		Assert.Equal(301.3, tick.GapMilliseconds);
		Assert.Equal(250, report.ThresholdMilliseconds);
		Assert.True(report.Supported);
	}

	/// <summary>Скачок пинга одного игрока отмечается не чаще раза в 30 секунд.</summary>
	[Fact]
	public void Скачки_пинга_прореживаются()
	{
		var history = new TelemetryHistory(TimeSpan.FromHours(24));
		for (var second = 0; second <= 40; second += 2)
		{
			var snapshot = Snapshot(("Alice", 500));
			snapshot["generated_at"] = second + 1;
			history.Record(snapshot, start.AddSeconds(second));
		}

		var spikes = history.Performance(start.AddSeconds(40), TimeSpan.FromHours(1)).PingSpikes;

		Assert.Equal(2, spikes.Count);
		Assert.Equal(30, spikes[1].Ts - spikes[0].Ts);
	}

	[Fact]
	public void Восстановление_из_файла_по_окну()
	{
		var history = new TelemetryHistory(TimeSpan.FromHours(1));
		var old = Snapshot(("Alice", 40));
		old["sampled_at"] = start.AddHours(-2).ToUnixTimeSeconds();
		var recent = Snapshot(("Alice", 40));
		recent["sampled_at"] = start.AddMinutes(-5).ToUnixTimeSeconds();

		history.Restore([old, recent], start);

		Assert.Equal(1, history.Count);
		Assert.Same(recent, history.Latest);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(2)]
	[InlineData(719)]
	[InlineData(720)]
	[InlineData(721)]
	[InlineData(1439)]
	[InlineData(1440)]
	[InlineData(1500)]
	[InlineData(43200)]
	public void Ряды_ограничены_и_сохраняют_края(int count)
	{
		var history = new TelemetryHistory(TimeSpan.FromDays(2));
		for (var index = 0; index < count; index++)
		{
			var sample = Snapshot(("Alice", index));
			sample["generated_at"] = index + 1;
			history.Record(sample, start.AddSeconds(index));
		}
		var now = start.AddSeconds(count);
		var pings = history.PlayerHistory(TelemetryPlayer.Create("alice").Value, now, TimeSpan.FromDays(2));
		var series = history.Performance(now, TimeSpan.FromDays(2)).Series;
		Assert.Equal(Math.Min(count, 720), pings.Count);
		Assert.Equal(pings.Select(point => point.Ts), series.Select(point => point.Ts));
		if (count > 0)
		{
			Assert.Equal(start.ToUnixTimeSeconds(), pings[0].Ts);
			Assert.Equal(start.AddSeconds(count - 1).ToUnixTimeSeconds(), pings[^1].Ts);
			Assert.Equal(pings.Count, pings.Select(point => point.Ts).Distinct().Count());
			Assert.Equal(pings.OrderBy(point => point.Ts), pings);
		}
	}

	[Fact]
	public void Оценка_пинга_берёт_последние_150_значений_и_первое_совпадение_имени()
	{
		var history = new TelemetryHistory(TimeSpan.FromHours(1));
		for (var index = 0; index < 160; index++)
		{
			history.Record(Snapshot(("Alice", index), ("ALICE", 999)), start.AddSeconds(index));
		}
		var player = history.Players(start.AddSeconds(159))[0];
		Assert.Equal(84.5, player["average_ping_ms"]!.GetValue<double>());
		Assert.Equal(159, player["max_ping_ms"]!.GetValue<double>());
		Assert.Equal(1, player["jitter_ms"]!.GetValue<double>());
	}

	[Fact]
	public void Истечение_истории_после_многих_записей_сохраняет_порядок()
	{
		var history = new TelemetryHistory(TimeSpan.FromSeconds(10));
		for (var index = 0; index < 3000; index++)
		{
			history.Record(Snapshot(("Alice", index)), start.AddSeconds(index));
		}
		Assert.Equal(11, history.Count);
		var pings = history.PlayerHistory(TelemetryPlayer.Create("Alice").Value, start.AddSeconds(2999), TimeSpan.FromHours(1));
		Assert.Equal(Enumerable.Range(2989, 11).Select(value => (double) value), pings.Select(point => point.PingMilliseconds));
		history.Record(Snapshot(("Alice", 1)), start.AddDays(1));
		Assert.Equal(1, history.Count);
	}

	[Fact]
	public void Числа_из_JSON_и_программных_значений_читаются_одинаково()
	{
		JsonNode?[] values = [JsonValue.Create(42L), JsonValue.Create(42), JsonValue.Create(42u),
			JsonValue.Create(42UL), JsonValue.Create(42m), JsonValue.Create(42f), JsonValue.Create((short)42),
			JsonNode.Parse("42"), JsonValue.Create(" 42 ")];
		Assert.All(values, value => Assert.Equal(42, TelemetryNumbers.Number(value)));
		Assert.Equal(1, TelemetryNumbers.Number(JsonValue.Create(true)));
		Assert.Equal(0, TelemetryNumbers.Number(JsonValue.Create(false)));
		Assert.Equal(7, TelemetryNumbers.Number(JsonValue.Create(double.NaN), 7));
		Assert.Equal(7, TelemetryNumbers.Number(JsonValue.Create(double.PositiveInfinity), 7));
		Assert.Equal(7, TelemetryNumbers.Number(JsonValue.Create("bad"), 7));
		Assert.Equal(7, TelemetryNumbers.Number(null, 7));
	}

	private static JsonObject Snapshot(params (string Name, double? Ping)[] players) => new()
	{
		["available"] = true,
		["stale"] = false,
		["players"] = new JsonArray([.. players.Select(player => (JsonNode?)new JsonObject
		{
			["name"] = player.Name,
			["ping_ms"] = player.Ping,
			["ip"] = "10.0.0.5",
		})]),
	};
}
