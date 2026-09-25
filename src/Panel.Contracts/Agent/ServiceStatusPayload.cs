using System.Text.Json.Serialization;

namespace Panel.Contracts.Agent;

/// <summary>
/// Состояние службы сервера. Форма заморожена: этот объект уходит в HTTP-ответ
/// <c>/api/status</c> (ADR-16).
/// </summary>
public sealed record ServiceStatusPayload
{
	[JsonPropertyName("active")] public bool Active { get; init; }
	[JsonPropertyName("state")] public string State { get; init; } = "unknown";
	[JsonPropertyName("substate")] public string SubState { get; init; } = "unknown";
	[JsonPropertyName("pid")] public int Pid { get; init; }
	[JsonPropertyName("memory")] public long Memory { get; init; }
	[JsonPropertyName("cpu_ns")] public long CpuNs { get; init; }
	[JsonPropertyName("restarts")] public int Restarts { get; init; }
	[JsonPropertyName("uptime")] public long Uptime { get; init; }

	/// <summary>Строка systemd <c>ExecMainStartTimestamp</c> как есть, для интерфейса.</summary>
	[JsonPropertyName("started")] public string Started { get; init; } = string.Empty;

	[JsonPropertyName("started_at")] public double StartedAt { get; init; }

	/// <summary>
	/// <c>boot_id:pid:start_ticks</c>: идентичность процесса одной строкой. В контракте это
	/// строка ради совместимости, в домене она разбирается в <c>ProcessIdentity</c>.
	/// </summary>
	[JsonPropertyName("instance")] public string Instance { get; init; } = string.Empty;

	[JsonPropertyName("unit")] public string Unit { get; init; } = string.Empty;
}
