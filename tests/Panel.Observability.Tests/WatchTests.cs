using System.Text.Json.Nodes;
using Panel.Observability.Domain;

namespace Panel.Observability.Tests;

/// <summary>Наблюдатели: события по замеру метрик и по снимку телеметрии.</summary>
public sealed class WatchTests
{
	private static readonly DateTimeOffset now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

	/// <summary>Первый замер не сообщает о переходе: сравнивать не с чем.</summary>
	[Fact]
	public void Остановка_и_возврат_только_при_смене()
	{
		var watch = new ServerWatch();

		Assert.DoesNotContain(watch.Observe(Metrics(active: true), now), IsTransition);
		Assert.DoesNotContain(watch.Observe(Metrics(active: true), now), IsTransition);

		var stopped = Assert.Single(watch.Observe(Metrics(active: false), now), IsTransition);
		Assert.Equal(("server.stopped", EventSeverity.Error), (stopped.Kind, stopped.Severity));

		var recovered = Assert.Single(watch.Observe(Metrics(active: true), now), IsTransition);
		Assert.Equal(("server.recovered", "Сервер снова работает"), (recovered.Kind, recovered.Title));

		static bool IsTransition(PanelEvent item) => item.Kind is "server.stopped" or "server.recovered";
	}

	[Fact]
	public void Мало_места_на_диске()
	{
		var metrics = Metrics();
		metrics["disk_warning"] = true;
		metrics["disk_free"] = 3L * 1024 * 1024 * 1024 + 5;

		var low = Assert.Single(new ServerWatch().Observe(metrics, now), item => item.Kind == "disk.low");

		Assert.Equal((EventSeverity.Critical, "Свободно 3 ГБ"), (low.Severity, low.Detail));
	}

	[Fact]
	public void Копия_старше_трёх_дней_или_её_нет()
	{
		var fresh = Metrics(backupAge: TimeSpan.FromDays(1));
		var old = Metrics(backupAge: TimeSpan.FromDays(4));
		var none = Metrics();
		none.Remove("last_backup");

		Assert.DoesNotContain(new ServerWatch().Observe(fresh, now), item => item.Kind == "backup.stale");
		Assert.Contains(new ServerWatch().Observe(old, now), item => item.Kind == "backup.stale");
		Assert.Contains(new ServerWatch().Observe(none, now), item => item.Kind == "backup.stale");
	}

	[Fact]
	public void Входы_и_выходы_игроков()
	{
		var watch = new TelemetryWatch();

		Assert.Empty(watch.Observe(Snapshot(("Alice", 40)), now));

		var changes = watch.Observe(Snapshot(("Bob", 40)), now);

		Assert.Equal(
			[("player.joined", "Bob"), ("player.left", "Alice")],
			changes.Select(item => (item.Kind, item.Detail)));
	}

	/// <summary>Если телеметрия замолчала, состояние сбрасывается без событий о выходе игроков.</summary>
	[Fact]
	public void Устаревшая_телеметрия_не_объявляет_выходов()
	{
		var watch = new TelemetryWatch();
		watch.Observe(Snapshot(("Alice", 40)), now);

		var stale = Snapshot();
		stale["stale"] = true;

		Assert.Empty(watch.Observe(stale, now));
		Assert.Empty(watch.Observe(Snapshot(("Bob", 40)), now));
	}

	/// <summary>Критический пинг сообщается на пятом плохом снимке подряд и один раз.</summary>
	[Fact]
	public void Критический_пинг_после_пяти_снимков()
	{
		var watch = new TelemetryWatch();
		var alerts = Enumerable.Range(0, 8)
			.SelectMany(_ => watch.Observe(Snapshot(("Alice", 450.5)), now))
			.Where(item => item.Kind == "player.ping_critical")
			.ToList();

		var alert = Assert.Single(alerts);
		Assert.Equal("Alice: 450 мс", alert.Detail);
		Assert.Equal("player.ping_critical:Alice", alert.NotificationKey);
	}

	[Fact]
	public void Хороший_снимок_обнуляет_счёт_пинга()
	{
		var watch = new TelemetryWatch();
		for (var i = 0; i < 4; i++)
		{
			watch.Observe(Snapshot(("Alice", 500)), now);
		}

		watch.Observe(Snapshot(("Alice", 40)), now);

		Assert.DoesNotContain(watch.Observe(Snapshot(("Alice", 500)), now), item => item.Kind == "player.ping_critical");
	}

	[Fact]
	public void Длинные_тики()
	{
		var snapshot = Snapshot();
		snapshot["long_tick_count"] = 3;
		snapshot["long_tick_threshold_ms"] = 250.4;

		var alert = Assert.Single(new TelemetryWatch().Observe(snapshot, now));

		Assert.Equal("3 длинных тиков подряд, порог 250 мс", alert.Detail);
		Assert.Equal("server.long_tick", alert.NotificationKey);
	}

	private static JsonObject Metrics(bool active = true, TimeSpan? backupAge = null) => new()
	{
		["active"] = active,
		["disk_warning"] = false,
		["last_backup"] = new JsonObject
		{
			["name"] = "vintagestory-a.tar.gz",
			["modified"] = (now - (backupAge ?? TimeSpan.FromHours(1))).ToUnixTimeSeconds(),
		},
	};

	private static JsonObject Snapshot(params (string Name, double Ping)[] players) => new()
	{
		["available"] = true,
		["stale"] = false,
		["players"] = new JsonArray([.. players.Select(player => (JsonNode?)new JsonObject
		{
			["name"] = player.Name,
			["ping_ms"] = player.Ping,
		})]),
	};
}
