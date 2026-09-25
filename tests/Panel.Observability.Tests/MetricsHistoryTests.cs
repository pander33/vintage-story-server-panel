using System.Text.Json.Nodes;
using Panel.Observability.Domain;

namespace Panel.Observability.Tests;

/// <summary>История метрик без ввода-вывода.</summary>
public sealed class MetricsHistoryTests
{
	private static readonly DateTimeOffset start = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

	/// <summary>Первый замер после старта даёт ноль: сравнивать не с чем.</summary>
	[Fact]
	public void Процессор_по_разнице_cpu_ns()
	{
		var history = new MetricsHistory(TimeSpan.FromDays(7));

		var first = history.Record(Sample(cpuNanoseconds: 1_000_000_000), start);
		var second = history.Record(Sample(cpuNanoseconds: 31_000_000_000), start.AddSeconds(60));

		Assert.Equal(0, first.CpuPercent);
		Assert.Equal(50, second.CpuPercent);
		Assert.Equal(50, history.Latest!["cpu_percent"]!.GetValue<double>());
	}

	[Fact]
	public void Доли_памяти_и_диска_и_игроки()
	{
		var point = new MetricsHistory(TimeSpan.FromDays(7)).Record(Sample(), start);

		Assert.Equal(25, point.MemoryPercent);
		Assert.Equal(12.5, point.DiskPercent);
		Assert.Equal(2, point.OnlinePlayers);
		Assert.Equal(19.8, point.ObservedTps);
	}

	[Fact]
	public void Без_итогов_доли_нулевые()
	{
		var sample = Sample();
		sample["memory_total"] = 0;
		sample["disk_total"] = 0;

		var point = new MetricsHistory(TimeSpan.FromDays(7)).Record(sample, start);

		Assert.Equal((0d, 0d), (point.MemoryPercent, point.DiskPercent));
	}

	/// <summary>Игроки считаются только по доступной телеметрии.</summary>
	[Fact]
	public void Недоступная_телеметрия_не_даёт_игроков()
	{
		var sample = Sample();
		sample["game"]!["available"] = false;

		Assert.Equal(0, new MetricsHistory(TimeSpan.FromDays(7)).Record(sample, start).OnlinePlayers);
	}

	[Fact]
	public void Строка_истории_в_порядке_ключей()
	{
		var line = new MetricsHistory(TimeSpan.FromDays(7)).Record(Sample(), start).ToJson();

		Assert.Equal(
			["ts", "cpu_percent", "memory", "memory_percent", "disk_used", "disk_percent", "world_size",
			 "restarts", "active", "online_players", "observed_tps", "average_tick_gap_ms"],
			line.Select(pair => pair.Key));
	}

	[Fact]
	public void Окно_прореживается_до_720_точек()
	{
		var history = new MetricsHistory(TimeSpan.FromDays(7));
		for (var minute = 0; minute < 1500; minute++)
		{
			history.Record(Sample(), start.AddMinutes(minute));
		}

		var window = history.Window(start.AddMinutes(1500), TimeSpan.FromDays(2));

		Assert.True(window.Count <= 750);
		Assert.Equal(start.ToUnixTimeSeconds(), window[0].Ts);
	}

	private static JsonObject Sample(long cpuNanoseconds = 0) => new()
	{
		["active"] = true,
		["cpu_ns"] = cpuNanoseconds,
		["memory"] = 1024,
		["memory_total"] = 4096,
		["disk_used"] = 100,
		["disk_total"] = 800,
		["world_size"] = 5,
		["restarts"] = 1,
		["game"] = new JsonObject
		{
			["available"] = true,
			["observed_tps"] = 19.8,
			["players"] = new JsonArray(new JsonObject { ["name"] = "a" }, new JsonObject { ["name"] = "b" }),
		},
	};
}
