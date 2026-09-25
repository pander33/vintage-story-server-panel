using System.Text.Json.Serialization;

namespace Panel.Contracts.Http;

/// <summary>
/// Тело ответа <c>GET /api/status</c>: состояние службы и поле <c>health</c>. Форма объявлена и
/// заморожена (ADR-16), изменения только дополняющие.
/// </summary>
public sealed record StatusResponse
{
	[JsonPropertyName("active")] public required bool Active { get; init; }
	[JsonPropertyName("state")] public required string State { get; init; }
	[JsonPropertyName("substate")] public required string SubState { get; init; }
	[JsonPropertyName("pid")] public required int Pid { get; init; }
	[JsonPropertyName("memory")] public required long Memory { get; init; }
	[JsonPropertyName("cpu_ns")] public required long CpuNs { get; init; }
	[JsonPropertyName("restarts")] public required int Restarts { get; init; }
	[JsonPropertyName("uptime")] public required long Uptime { get; init; }
	[JsonPropertyName("started")] public required string Started { get; init; }
	[JsonPropertyName("started_at")] public required double StartedAt { get; init; }
	[JsonPropertyName("instance")] public required string Instance { get; init; }
	[JsonPropertyName("unit")] public required string Unit { get; init; }
	[JsonPropertyName("health")] public required HealthResponse Health { get; init; }
}

/// <summary>
/// Вердикт готовности.
/// </summary>
/// <remarks>
/// Шесть ключей есть всегда, а отсутствие проверки выражается значением
/// <c>state: "unknown"</c>.
/// </remarks>
public sealed record HealthResponse
{
	[JsonPropertyName("checked_at")] public required long CheckedAt { get; init; }
	[JsonPropertyName("instance")] public required string Instance { get; init; }
	[JsonPropertyName("active")] public required bool Active { get; init; }
	[JsonPropertyName("state")] public required string State { get; init; }
	[JsonPropertyName("detail")] public required string Detail { get; init; }
	[JsonPropertyName("failures")] public required int Failures { get; init; }

	/// <summary>
	/// Почему автоперезапуск не выполнен. Ключ появляется в <c>health</c> только при наличии
	/// причины, поэтому <c>null</c> не пишется.
	/// </summary>
	[JsonPropertyName("recovery_blocked")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? RecoveryBlocked { get; init; }
}
