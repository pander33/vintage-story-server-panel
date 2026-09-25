using System.Text.Json.Serialization;

namespace Panel.Contracts.Content;

/// <summary>Запись истории.</summary>
public sealed record HistoryItemPayload
{
	[JsonPropertyName("key")] public string Key { get; init; } = string.Empty;
	[JsonPropertyName("group")] public string Group { get; init; } = string.Empty;
	[JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
	[JsonPropertyName("size")] public long Size { get; init; }
	[JsonPropertyName("modified")] public long Modified { get; init; }
}

/// <summary>
/// Ответ <c>GET /api/history</c>: три хранилища и место, занятое каждым. Ключи словарей —
/// имена хранилищ: <c>mod-history</c>, <c>config-backup</c>, <c>mod-config-backup</c>.
/// </summary>
public sealed record HistoryPayload
{
	[JsonPropertyName("stores")]
	public IReadOnlyDictionary<string, IReadOnlyList<HistoryItemPayload>> Stores { get; init; } =
		new Dictionary<string, IReadOnlyList<HistoryItemPayload>>();

	[JsonPropertyName("bytes")] public IReadOnlyDictionary<string, long> Bytes { get; init; } = new Dictionary<string, long>();
}

public sealed record HistoryDeletePayload
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;
	[JsonPropertyName("store")] public string Store { get; init; } = string.Empty;
	[JsonPropertyName("key")] public string Key { get; init; } = string.Empty;
	[JsonPropertyName("freed")] public long Freed { get; init; }
}

public sealed record HistoryPrunePayload
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;
	[JsonPropertyName("store")] public string Store { get; init; } = string.Empty;
	[JsonPropertyName("keep")] public int Keep { get; init; }
	[JsonPropertyName("removed")] public IReadOnlyList<string> Removed { get; init; } = [];
	[JsonPropertyName("freed")] public long Freed { get; init; }
}
