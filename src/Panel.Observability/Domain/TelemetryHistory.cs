using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.SharedKernel;

namespace Panel.Observability.Domain;

/// <summary>Длинный тик из снимка.</summary>
public sealed record LongTick(long At, double GapMilliseconds);

/// <summary>Пинг одного игрока в одном снимке.</summary>
public sealed record PingReading(string Name, double? PingMilliseconds);

/// <summary>
/// Выжимка одного снимка телеметрии: только данные для отчётов.
/// </summary>
/// <remarks>
/// При опросе раз в две секунды за сутки набирается 43 200 снимков на сервер, и целиком, как
/// <see cref="JsonObject"/>, они заняли бы сотни мегабайт. Поэтому в памяти лежит выжимка, а в
/// файл пишется полный снимок: формат истории заморожен (ADR-04).
/// </remarks>
public sealed record TelemetryPoint(
	long SampledAt,
	bool Available,
	bool Stale,
	double? GeneratedAt,
	double ObservedTps,
	double MaximumTickGapMilliseconds,
	int LongTickCount,
	IReadOnlyList<LongTick> LongTicks,
	IReadOnlyList<PingReading> Players)
{
	/// <summary>Выжимка из строки истории или свежего снимка.</summary>
	public static TelemetryPoint FromSample(JsonObject sample)
	{
		ArgumentNullException.ThrowIfNull(sample);

		var sampled = (long) TelemetryNumbers.Number(sample["sampled_at"]);

		return new TelemetryPoint(
			sampled,
			TelemetryNumbers.Truthy(sample["available"]),
			TelemetryNumbers.Truthy(sample["stale"]),
			sample["generated_at"] is { } generated && TelemetryNumbers.Truthy(generated)
				? TelemetryNumbers.Number(generated)
				: null,
			TelemetryNumbers.Number(sample["observed_tps"]),
			TelemetryNumbers.Number(sample["maximum_tick_gap_ms"]),
			(int) TelemetryNumbers.Number(sample["long_tick_count"]),
			sample["long_ticks"] is JsonArray ticks
				? [.. ticks.OfType<JsonObject>().Select(tick => new LongTick(
					(long)TelemetryNumbers.Number(tick["at"], sampled) is var at && at != 0 ? at : sampled,
					Math.Round(TelemetryNumbers.Number(tick["gap_ms"]), 1)))]
				: [],
			sample["players"] is JsonArray players
				? [.. players.OfType<JsonObject>().Select(player => new PingReading(
					TelemetryNumbers.Text(player["name"]),
					player["ping_ms"] is { } ping && ping.GetValueKind() != JsonValueKind.Null
						? TelemetryNumbers.Number(ping)
						: null))]
				: []);
	}
}

/// <summary>
/// Разбор значений снимка: число, числовая строка или логическое значение, иначе умолчание.
/// Бесконечность и NaN тоже дают умолчание.
/// </summary>
public static class TelemetryNumbers
{
	public static double Number(JsonNode? node, double fallback = 0)
	{
		if (node is not JsonValue value)
		{
			return fallback;
		}

		double result;
		switch (value.GetValueKind())
		{
			// Через текст JSON, а не GetValue<double>(): для значения, созданного из long (sampled_at
			// ставит сама панель), GetValue<double>() бросает исключение.
			case JsonValueKind.Number:
				result = double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
				break;
			case JsonValueKind.True:
				return 1;
			case JsonValueKind.False:
				return 0;
			case JsonValueKind.String when double.TryParse(
				value.GetValue<string>().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
				result = parsed;
				break;
			default:
				return fallback;
		}

		return double.IsFinite(result) ? result : fallback;
	}

	/// <summary>Истинность значения: <c>0</c>, <c>""</c>, <c>false</c>, <c>null</c> ложны.</summary>
	public static bool Truthy(JsonNode? node) => node switch
	{
		null => false,
		JsonValue value => value.GetValueKind() switch
		{
			JsonValueKind.True => true,
			JsonValueKind.Number => Number(value) != 0,
			JsonValueKind.String => value.GetValue<string>().Length > 0,
			_ => false,
		},
		JsonObject map => map.Count > 0,
		JsonArray list => list.Count > 0,
		_ => false,
	};

	/// <summary><c>str(value)</c> для имени: строка как есть, остальное текстом JSON.</summary>
	public static string Text(JsonNode? node) => node switch
	{
		null => string.Empty,
		JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
		_ => node.ToJsonString(),
	};
}

/// <summary>Качество соединения игрока.</summary>
public static class ConnectionQuality
{
	public static string Of(double? pingMilliseconds, double jitterMilliseconds)
	{
		if (pingMilliseconds is not { } ping)
		{
			return "unavailable";
		}

		if (ping <= 50 && jitterMilliseconds <= 20)
		{
			return "excellent";
		}

		if (ping <= 100 && jitterMilliseconds <= 40)
		{
			return "good";
		}

		return ping <= 250 && jitterMilliseconds <= 100 ? "unstable" : "poor";
	}
}

/// <summary>Точка графика пинга одного игрока.</summary>
public sealed record PingSample(long Ts, double PingMilliseconds);

/// <summary>
/// Имя игрока в запросе истории телеметрии.
/// </summary>
public readonly record struct TelemetryPlayer
{
	private TelemetryPlayer(string name) => Name = name;

	public string Name { get; }

	public bool IsEmpty => string.IsNullOrEmpty(Name);

	public static Result<TelemetryPlayer> Create(string? raw)
	{
		var value = raw ?? string.Empty;
		return value.EnumerateRunes().Count() > 32
			? Failure.Invalid("Имя игрока слишком длинное")
			: new TelemetryPlayer(value);
	}
}

/// <summary>Точка ряда производительности.</summary>
public sealed record PerformancePoint(long Ts, double Tps, double MaxGapMilliseconds, int LongTicks);

/// <summary>Скачок пинга выше критического порога.</summary>
public sealed record PingSpike(long Ts, string Player, double PingMilliseconds);

/// <summary>Отчёт для графика производительности.</summary>
public sealed record PerformanceReport(
	IReadOnlyList<PerformancePoint> Series,
	IReadOnlyList<LongTick> LongTicks,
	IReadOnlyList<PingSpike> PingSpikes,
	double? ThresholdMilliseconds,
	bool Supported,
	int PingCriticalMilliseconds);

/// <summary>
/// История телеметрии одного сервера: окно хранения, текущие игроки с оценкой соединения,
/// ряды для графиков.
/// </summary>
/// <remarks>
/// Без ввода-вывода: файл истории читает и пишет инфраструктура, сюда приходят разобранные
/// снимки и текущее время.
/// </remarks>
public sealed class TelemetryHistory
{
	/// <summary>Порог критического пинга в мс.</summary>
	public const int PingCriticalMilliseconds = 400;

	/// <summary>Сколько точек отдаёт ряд; больше график не покажет.</summary>
	private const int maxSeries = 720;

	private const int spikeFromMilliseconds = 250;

	private static readonly TimeSpan playerWindow = TimeSpan.FromMinutes(5);

	private readonly List<TelemetryPoint> points = [];

	public TelemetryHistory(TimeSpan maxAge)
	{
		MaxAge = maxAge;
	}

	public TimeSpan MaxAge { get; }

	/// <summary>Последний снимок целиком, с <c>sampled_at</c>.</summary>
	public JsonObject? Latest { get; private set; }

	public int Count => points.Count;

	/// <summary>Снимки из файла истории, отобранные по окну хранения.</summary>
	public void Restore(IEnumerable<JsonObject> samples, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(samples);

		var cutoff = now.ToUnixTimeSeconds() - (long) MaxAge.TotalSeconds;
		foreach (var sample in samples)
		{
			var point = TelemetryPoint.FromSample(sample);
			if (point.SampledAt >= cutoff)
			{
				points.Add(point);
				Latest = sample;
			}
		}
	}

	/// <summary>
	/// Принять свежий снимок: проставить <c>sampled_at</c>, добавить, отбросить устаревшие.
	/// Возвращает снимок в том виде, в каком он пишется в историю.
	/// </summary>
	public JsonObject Record(JsonObject sample, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(sample);

		var stored = (JsonObject) sample.DeepClone();
		var sampledAt = now.ToUnixTimeSeconds();
		stored["sampled_at"] = sampledAt;

		Latest = stored;
		points.Add(TelemetryPoint.FromSample(stored));

		var cutoff = sampledAt - (long) MaxAge.TotalSeconds;
		var expired = points.FindIndex(point => point.SampledAt >= cutoff);
		points.RemoveRange(0, expired < 0 ? points.Count : expired);

		return stored;
	}

	/// <summary>Текущий снимок с игроками, дополненными оценкой соединения.</summary>
	public JsonObject Current(DateTimeOffset now)
	{
		var value = Latest is null
			? new JsonObject { ["available"] = false, ["stale"] = true }
			: (JsonObject) Latest.DeepClone();

		value["players"] = new JsonArray([.. Players(now)]);
		return value;
	}

	/// <summary>
	/// Игроки последнего снимка с пингом за пять минут: среднее, максимум, дрожание, число
	/// скачков, качество. Устаревший снимок игроков не показывает: это значит «неизвестно», а не
	/// «все вышли».
	/// </summary>
	public IReadOnlyList<JsonObject> Players(DateTimeOffset now)
	{
		if (Latest is null || !TelemetryNumbers.Truthy(Latest["available"]) || TelemetryNumbers.Truthy(Latest["stale"]))
		{
			return [];
		}

		var cutoff = now.ToUnixTimeSeconds() - (long) playerWindow.TotalSeconds;
		var result = new List<JsonObject>();

		foreach (var player in (Latest["players"] as JsonArray ?? []).OfType<JsonObject>())
		{
			var name = TelemetryNumbers.Text(player["name"]);
			var pings = PingsOf(name, cutoff).TakeLast(150).Select(sample => sample.PingMilliseconds).ToList();
			double? current = player["ping_ms"] is { } ping && ping.GetValueKind() != JsonValueKind.Null
				? TelemetryNumbers.Number(ping)
				: null;

			var jitter = pings.Zip(pings.Skip(1), (a, b) => Math.Abs(b - a)).Sum() / Math.Max(1, pings.Count - 1);

			var enriched = (JsonObject) player.DeepClone();
			enriched["ping_ms"] = current is { } now1 ? Math.Round(now1, 1) : null;
			enriched["average_ping_ms"] = pings.Count > 0 ? Math.Round(pings.Average(), 1) : current;
			enriched["max_ping_ms"] = pings.Count > 0 ? Math.Round(pings.Max(), 1) : current;
			enriched["jitter_ms"] = Math.Round(jitter, 1);
			enriched["spike_count"] = pings.Count(value => value >= spikeFromMilliseconds);
			enriched["quality"] = ConnectionQuality.Of(current, jitter);
			result.Add(enriched);
		}

		return [.. result.OrderBy(item => TelemetryNumbers.Text(item["name"]).ToLowerInvariant(), StringComparer.Ordinal)];
	}

	/// <summary>Пинг игрока за окно для графика на вкладке «Игроки».</summary>
	public IReadOnlyList<PingSample> PlayerHistory(TelemetryPlayer player, DateTimeOffset now, TimeSpan window)
	{
		if (player.IsEmpty)
		{
			return [];
		}

		return Downsample(PingsOf(player.Name, now.ToUnixTimeSeconds() - (long) window.TotalSeconds).ToList());
	}

	/// <summary>
	/// Ряды для графика производительности. Мод пишет и панель опрашивает раз в две секунды, но
	/// не синхронно, поэтому один снимок может быть прочитан дважды; учитывается он один раз, по
	/// <c>generated_at</c>.
	/// </summary>
	public PerformanceReport Performance(DateTimeOffset now, TimeSpan window)
	{
		var cutoff = now.ToUnixTimeSeconds() - (long) window.TotalSeconds;
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var series = new List<PerformancePoint>();
		var longTicks = new List<LongTick>();
		var spikes = new List<PingSpike>();
		var lastSpike = new Dictionary<string, long>(StringComparer.Ordinal);

		foreach (var point in points)
		{
			if (point.SampledAt < cutoff || !point.Available || point.Stale)
			{
				continue;
			}

			var key = point.GeneratedAt is { } generated
				? generated.ToString("R", CultureInfo.InvariantCulture)
				: "s:" + point.SampledAt.ToString(CultureInfo.InvariantCulture);
			if (!seen.Add(key))
			{
				continue;
			}

			series.Add(new PerformancePoint(
				point.SampledAt,
				Math.Round(point.ObservedTps, 2),
				Math.Round(point.MaximumTickGapMilliseconds, 1),
				point.LongTickCount));
			longTicks.AddRange(point.LongTicks);

			foreach (var player in point.Players)
			{
				if (player.Name.Length == 0 || player.PingMilliseconds is not { } ping || ping < PingCriticalMilliseconds)
				{
					continue;
				}

				if (point.SampledAt - lastSpike.GetValueOrDefault(player.Name) < 30)
				{
					continue;
				}

				lastSpike[player.Name] = point.SampledAt;
				spikes.Add(new PingSpike(point.SampledAt, player.Name, Math.Round(ping, 1)));
			}
		}

		var threshold = TelemetryNumbers.Number(Latest?["long_tick_threshold_ms"]);

		return new PerformanceReport(
			Downsample(series),
			longTicks.TakeLast(200).ToList(),
			spikes.TakeLast(200).ToList(),
			threshold != 0 ? Math.Round(threshold, 1) : null,
			threshold > 0,
			PingCriticalMilliseconds);
	}

	private IEnumerable<PingSample> PingsOf(string name, long cutoff)
	{
		var key = name.ToLowerInvariant();

		foreach (var point in points)
		{
			if (point.SampledAt < cutoff || !point.Available)
			{
				continue;
			}

			var player = point.Players.FirstOrDefault(item => item.Name.ToLowerInvariant() == key);
			if (player?.PingMilliseconds is { } ping)
			{
				yield return new PingSample(point.SampledAt, ping);
			}
		}
	}

	/// <summary>Не больше 720 точек: каждая n-я, как <c>series[::step]</c>.</summary>
	private static IReadOnlyList<T> Downsample<T>(IReadOnlyList<T> items)
	{
		if (items.Count <= maxSeries)
		{
			return items;
		}

		var step = Math.Max(1, items.Count / maxSeries);
		return [.. items.Where((_, index) => index % step == 0)];
	}
}
