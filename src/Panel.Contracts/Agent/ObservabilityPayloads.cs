using System.Text.Json.Serialization;

namespace Panel.Contracts.Agent;

/// <summary>Одно событие из <c>events.jsonl</c> мода.</summary>
public sealed record GameEventPayload
{
	[JsonPropertyName("at")] public long At { get; init; }
	[JsonPropertyName("seq")] public long Seq { get; init; }
	[JsonPropertyName("kind")] public string Kind { get; init; } = string.Empty;
	[JsonPropertyName("player")] public string Player { get; init; } = string.Empty;
	[JsonPropertyName("uid")] public string Uid { get; init; } = string.Empty;
	[JsonPropertyName("channel")] public long? Channel { get; init; }
	[JsonPropertyName("text")] public string Text { get; init; } = string.Empty;

	/// <summary>
	/// Причина смерти по-русски. Это текст интерфейса, поэтому его добавляет веб-процесс, а не
	/// демон. У прочих событий <c>null</c>.
	/// </summary>
	[JsonPropertyName("detail")] public string? Detail { get; init; }
}

public sealed record GameEventsPayload
{
	[JsonPropertyName("events")] public IReadOnlyList<GameEventPayload> Events { get; init; } = [];

	/// <summary>Пишет ли мод журнал событий. Старый мод не пишет, и пустая лента не значит, что событий не было.</summary>
	[JsonPropertyName("supported")] public bool Supported { get; init; }
}

/// <summary>Ответ <c>player-action</c>: действие и цель, для гейммода также режим.</summary>
public sealed record PlayerActionPayload
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;
	[JsonPropertyName("action")] public string Action { get; init; } = string.Empty;

	/// <summary>Имя игрока; у объявления <c>null</c>.</summary>
	[JsonPropertyName("target")] public string? Target { get; init; }

	[JsonPropertyName("mode")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Mode { get; init; }
}
