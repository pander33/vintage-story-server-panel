using System.Text.Json.Nodes;

namespace Panel.Observability.Domain;

/// <summary>
/// Точка истории метрик: строка <c>metrics.jsonl</c>.
/// </summary>
public sealed record MetricsPoint(
	long Ts,
	double CpuPercent,
	long Memory,
	double MemoryPercent,
	long DiskUsed,
	double DiskPercent,
	long WorldSize,
	long Restarts,
	bool Active,
	int OnlinePlayers,
	double ObservedTps,
	double AverageTickGapMilliseconds)
{
	/// <summary>Строка истории с фиксированным порядком ключей.</summary>
	public JsonObject ToJson() => new()
	{
		["ts"] = Ts,
		["cpu_percent"] = CpuPercent,
		["memory"] = Memory,
		["memory_percent"] = MemoryPercent,
		["disk_used"] = DiskUsed,
		["disk_percent"] = DiskPercent,
		["world_size"] = WorldSize,
		["restarts"] = Restarts,
		["active"] = Active,
		["online_players"] = OnlinePlayers,
		["observed_tps"] = ObservedTps,
		["average_tick_gap_ms"] = AverageTickGapMilliseconds,
	};

	public static MetricsPoint? FromJson(JsonObject line)
	{
		ArgumentNullException.ThrowIfNull(line);

		if (line["ts"] is null)
		{
			return null;
		}

		return new MetricsPoint(
			(long) TelemetryNumbers.Number(line["ts"]),
			TelemetryNumbers.Number(line["cpu_percent"]),
			(long) TelemetryNumbers.Number(line["memory"]),
			TelemetryNumbers.Number(line["memory_percent"]),
			(long) TelemetryNumbers.Number(line["disk_used"]),
			TelemetryNumbers.Number(line["disk_percent"]),
			(long) TelemetryNumbers.Number(line["world_size"]),
			(long) TelemetryNumbers.Number(line["restarts"]),
			TelemetryNumbers.Truthy(line["active"]),
			(int) TelemetryNumbers.Number(line["online_players"]),
			TelemetryNumbers.Number(line["observed_tps"]),
			TelemetryNumbers.Number(line["average_tick_gap_ms"]));
	}
}

/// <summary>
/// История метрик сервера: процессор, память, диск, размер мира, игроки.
/// </summary>
/// <remarks>
/// Без ввода-вывода. Загрузка процессора считается по разнице <c>cpu_ns</c> службы между
/// двумя замерами, поэтому первый замер после старта панели даёт ноль.
/// </remarks>
public sealed class MetricsHistory
{
	private const int maxPoints = 720;

	private readonly List<MetricsPoint> points = [];
	private (DateTimeOffset At, long CpuNanoseconds)? lastCpu;

	public MetricsHistory(TimeSpan maxAge)
	{
		MaxAge = maxAge;
	}

	public TimeSpan MaxAge { get; }

	/// <summary>Последний ответ демона с <c>cpu_percent</c>; его показывает «Обзор».</summary>
	public JsonObject? Latest { get; private set; }

	public IReadOnlyList<MetricsPoint> Points => points;

	public void Restore(IEnumerable<JsonObject> lines, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(lines);

		var cutoff = now.ToUnixTimeSeconds() - (long) MaxAge.TotalSeconds;
		foreach (var line in lines)
		{
			if (MetricsPoint.FromJson(line) is { } point && point.Ts >= cutoff)
			{
				points.Add(point);
			}
		}
	}

	/// <summary>Принять замер демона и вернуть точку для записи в историю.</summary>
	public MetricsPoint Record(JsonObject current, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(current);

		var cpu = (long) TelemetryNumbers.Number(current["cpu_ns"]);
		var cpuPercent = 0d;
		if (lastCpu is { } previous)
		{
			var elapsed = Math.Max(0.001, (now - previous.At).TotalSeconds);
			cpuPercent = Math.Max(0, (cpu - previous.CpuNanoseconds) / (elapsed * 1_000_000_000d) * 100);
		}

		lastCpu = (now, cpu);

		var memory = (long) TelemetryNumbers.Number(current["memory"]);
		var memoryTotal = (long) TelemetryNumbers.Number(current["memory_total"]);
		var diskUsed = (long) TelemetryNumbers.Number(current["disk_used"]);
		var diskTotal = (long) TelemetryNumbers.Number(current["disk_total"]);
		var game = current["game"] as JsonObject;
		var gameAvailable = game is not null && TelemetryNumbers.Truthy(game["available"]);

		var point = new MetricsPoint(
			now.ToUnixTimeSeconds(),
			Math.Round(cpuPercent, 2),
			memory,
			memoryTotal != 0 ? Math.Round((double) memory / memoryTotal * 100, 2) : 0,
			diskUsed,
			diskTotal != 0 ? Math.Round((double) diskUsed / diskTotal * 100, 2) : 0,
			(long) TelemetryNumbers.Number(current["world_size"]),
			(long) TelemetryNumbers.Number(current["restarts"]),
			TelemetryNumbers.Truthy(current["active"]),
			gameAvailable && game!["players"] is JsonArray players ? players.Count : 0,
			TelemetryNumbers.Number(game?["observed_tps"]),
			TelemetryNumbers.Number(game?["average_tick_gap_ms"]));

		var latest = (JsonObject) current.DeepClone();
		latest["cpu_percent"] = point.CpuPercent;
		Latest = latest;

		points.Add(point);
		var cutoff = point.Ts - (long) MaxAge.TotalSeconds;
		var expired = points.FindIndex(item => item.Ts >= cutoff);
		points.RemoveRange(0, expired < 0 ? points.Count : expired);

		return point;
	}

	/// <summary>Точки за окно, не больше 720: берётся каждая n-я.</summary>
	public IReadOnlyList<MetricsPoint> Window(DateTimeOffset now, TimeSpan window)
	{
		var cutoff = now.ToUnixTimeSeconds() - (long) window.TotalSeconds;
		var selected = points.Where(point => point.Ts >= cutoff).ToList();
		if (selected.Count <= maxPoints)
		{
			return selected;
		}

		var step = Math.Max(1, selected.Count / maxPoints);
		return [.. selected.Where((_, index) => index % step == 0)];
	}
}
