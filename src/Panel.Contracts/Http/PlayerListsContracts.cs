using System.Text.Json.Serialization;

namespace Panel.Contracts.Http;

/// <summary>
/// Ответ <c>GET /api/players/lists</c>: списки бана и белого списка,
/// прочитанные демоном из файлов игры, и режим белого списка из <c>serverconfig.json</c>.
/// </summary>
public sealed record PlayerListsResponse
{
	[JsonPropertyName("banned")] public required IReadOnlyList<PlayerListEntryResponse> Banned { get; init; }
	[JsonPropertyName("whitelisted")] public required IReadOnlyList<PlayerListEntryResponse> Whitelisted { get; init; }
	[JsonPropertyName("whitelist_mode")] public required bool WhitelistMode { get; init; }

	/// <summary>
	/// Не удалось прочитать <c>serverconfig.json</c>: списки верны, но режим белого списка
	/// неизвестен и показан как выключенный.
	/// </summary>
	[JsonPropertyName("stale")] public bool Stale { get; init; }
}

public sealed record PlayerListEntryResponse
{
	[JsonPropertyName("player")] public required string Player { get; init; }

	/// <summary>
	/// Только при праве <c>SeePlayerAddresses</c> (та же приватность, что у сетевых адресов);
	/// иначе пустая строка.
	/// </summary>
	[JsonPropertyName("uid")] public string Uid { get; init; } = string.Empty;

	[JsonPropertyName("issued_by")] public string? IssuedBy { get; init; }
	[JsonPropertyName("until")] public long? Until { get; init; }
	[JsonPropertyName("forever")] public bool Forever { get; init; }
	[JsonPropertyName("reason")] public string? Reason { get; init; }
}
