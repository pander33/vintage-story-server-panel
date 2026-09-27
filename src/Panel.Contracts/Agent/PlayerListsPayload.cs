using System.Text.Json.Serialization;

namespace Panel.Contracts.Agent;

/// <summary>
/// Ответ операции демона <c>players-lists</c> (интерфейс 2.1 §3): оба списка как есть, без учёта
/// прав панели — приватность UID решает панель, а не демон.
/// </summary>
public sealed record PlayerListsPayload
{
	[JsonPropertyName("banned")] public required IReadOnlyList<PlayerListEntryPayload> Banned { get; init; }
	[JsonPropertyName("whitelisted")] public required IReadOnlyList<PlayerListEntryPayload> Whitelisted { get; init; }
}

public sealed record PlayerListEntryPayload
{
	[JsonPropertyName("player")] public required string Player { get; init; }
	[JsonPropertyName("uid")] public string? Uid { get; init; }
	[JsonPropertyName("issued_by")] public string? IssuedBy { get; init; }
	[JsonPropertyName("until")] public long? Until { get; init; }
	[JsonPropertyName("forever")] public bool Forever { get; init; }
	[JsonPropertyName("reason")] public string? Reason { get; init; }
}
