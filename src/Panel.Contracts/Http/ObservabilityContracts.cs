using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Panel.Contracts.Http;

/// <summary>
/// Ответ <c>GET /api/players</c>. Игроки онлайн — объекты снимка телеметрии как есть с
/// оценкой соединения; без телеметрии — выведенные из журнала <c>{name, since}</c>. Форма
/// элементов в двух случаях разная, фронтенд это учитывает.
/// </summary>
public sealed record PlayersResponse
{
	[JsonPropertyName("online")] public required IReadOnlyList<JsonObject> Online { get; init; }
	[JsonPropertyName("events")] public required IReadOnlyList<LogEventResponse> Events { get; init; }
	[JsonPropertyName("commands")] public required CommandCapabilityResponse Commands { get; init; }
	[JsonPropertyName("telemetry")] public required TelemetryStateResponse Telemetry { get; init; }
}

/// <summary>Четыре поля свежести снимка: <c>available, stale, detail, age_seconds</c>.</summary>
public sealed record TelemetryStateResponse
{
	[JsonPropertyName("available")] public JsonNode? Available { get; init; }
	[JsonPropertyName("stale")] public JsonNode? Stale { get; init; }
	[JsonPropertyName("detail")] public JsonNode? Detail { get; init; }
	[JsonPropertyName("age_seconds")] public JsonNode? AgeSeconds { get; init; }
}

/// <summary>Ответ <c>GET /api/telemetry</c>: текущий снимок и пинг выбранного игрока.</summary>
public sealed record TelemetryResponse
{
	[JsonPropertyName("current")] public required JsonObject Current { get; init; }
	[JsonPropertyName("history")] public required IReadOnlyList<PingPointResponse> History { get; init; }
	[JsonPropertyName("interval")] public required int Interval { get; init; }
}

public sealed record PingPointResponse
{
	[JsonPropertyName("ts")] public long Ts { get; init; }
	[JsonPropertyName("ping_ms")] public double PingMilliseconds { get; init; }
}

/// <summary>Ответ <c>GET /api/performance</c>: ряды тиков и события журнала на одной оси.</summary>
public sealed record PerformanceResponse
{
	[JsonPropertyName("series")] public required IReadOnlyList<PerformancePointResponse> Series { get; init; }
	[JsonPropertyName("long_ticks")] public required IReadOnlyList<LongTickResponse> LongTicks { get; init; }
	[JsonPropertyName("ping_spikes")] public required IReadOnlyList<PingSpikeResponse> PingSpikes { get; init; }
	[JsonPropertyName("threshold_ms")] public double? ThresholdMilliseconds { get; init; }
	[JsonPropertyName("supported")] public bool Supported { get; init; }
	[JsonPropertyName("ping_critical_ms")] public int PingCriticalMilliseconds { get; init; }
	[JsonPropertyName("overloads")] public required IReadOnlyList<string> Overloads { get; init; }
	[JsonPropertyName("errors")] public required IReadOnlyList<string> Errors { get; init; }
	[JsonPropertyName("mod_errors")] public required IReadOnlyList<string> ModErrors { get; init; }
	[JsonPropertyName("player_events")] public required IReadOnlyList<LogEventResponse> PlayerEvents { get; init; }
	[JsonPropertyName("minutes")] public int Minutes { get; init; }
}

public sealed record PerformancePointResponse
{
	[JsonPropertyName("ts")] public long Ts { get; init; }
	[JsonPropertyName("tps")] public double Tps { get; init; }
	[JsonPropertyName("max_gap_ms")] public double MaxGapMilliseconds { get; init; }
	[JsonPropertyName("long_ticks")] public int LongTicks { get; init; }
}

public sealed record LongTickResponse
{
	[JsonPropertyName("ts")] public long Ts { get; init; }
	[JsonPropertyName("gap_ms")] public double GapMilliseconds { get; init; }
}

public sealed record PingSpikeResponse
{
	[JsonPropertyName("ts")] public long Ts { get; init; }
	[JsonPropertyName("player")] public string Player { get; init; } = string.Empty;
	[JsonPropertyName("ping_ms")] public double PingMilliseconds { get; init; }
}

/// <summary>
/// Ответ <c>GET /api/metrics</c>: последний замер демона целиком и точки истории (строки
/// <c>metrics.jsonl</c> как есть).
/// </summary>
public sealed record MetricsResponse
{
	[JsonPropertyName("current")] public JsonObject? Current { get; init; }
	[JsonPropertyName("points")] public required IReadOnlyList<JsonObject> Points { get; init; }
	[JsonPropertyName("interval")] public required int Interval { get; init; }
}
