using System.Text.Json;
using System.Text.Json.Serialization;
using Panel.SharedKernel;

namespace Panel.Contracts.Agent;

/// <summary>
/// Ответ команды <c>telemetry</c>: снимок мода целиком и оценка свежести. Все поля
/// необязательны (INV-35): мод телеметрии версионируется отдельно от панели (ADR-10) и может
/// быть старше.
/// </summary>
/// <remarks>
/// <para>
/// Демон отдаёт <c>telemetry.json</c> как есть, добавив <c>available</c>, <c>stale</c>,
/// <c>age_seconds</c> и <c>detail</c>, а фронтенд читает из снимка десятки полей:
/// <c>observed_tps</c>, <c>long_ticks</c>, <c>world</c>, байты сети. Поэтому типизированы
/// только поля, нужные самой панели, а остальные передаются через <see cref="Extra"/>.
/// </para>
/// <para>
/// Имена те, что пишет мод (<c>JsonNamingPolicy.SnakeCaseLower</c>): <c>observed_tps</c>,
/// <c>ping_ms</c>, <c>ip</c>.
/// </para>
/// </remarks>
public sealed record TelemetryPayload
{
	[JsonPropertyName("available")] public bool Available { get; init; }
	[JsonPropertyName("stale")] public bool Stale { get; init; } = true;
	[JsonPropertyName("age_seconds")] public long? AgeSeconds { get; init; }
	[JsonPropertyName("detail")] public Phrase? Detail { get; init; }
	[JsonPropertyName("generated_at")] public double? GeneratedAt { get; init; }
	[JsonPropertyName("observed_tps")] public double? TicksPerSecond { get; init; }
	[JsonPropertyName("players")] public IReadOnlyList<PlayerPayload> Players { get; init; } = [];

	/// <summary>
	/// Раздел <c>world</c> снимка как есть: время, календарь и живой <c>World.Config</c>
	/// (<c>world.configuration</c>, мод 0.2.0 и новее).
	/// </summary>
	[JsonPropertyName("world")] public System.Text.Json.Nodes.JsonObject? World { get; init; }

	/// <summary>Поддерживает ли мод журнал игровых событий (<c>events.jsonl</c>).</summary>
	[JsonPropertyName("events_supported")] public bool? EventsSupported { get; init; }

	/// <summary>Остальные поля снимка без разбора, для фронтенда.</summary>
	[JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// Игрок в снимке телеметрии. <c>Address</c> есть в контракте, но наружу проходит только через
/// маскирование: наблюдателю адрес не показывается (INV-38).
/// </summary>
public sealed record PlayerPayload
{
	[JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
	[JsonPropertyName("uid")] public string? Uid { get; init; }
	[JsonPropertyName("ping_ms")] public double? Ping { get; init; }
	[JsonPropertyName("ip")] public string? Address { get; init; }

	/// <summary><c>client_id</c>, <c>state</c>, <c>chunk_radius</c>, <c>role</c> как есть.</summary>
	[JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
}
