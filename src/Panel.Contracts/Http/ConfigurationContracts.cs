using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Panel.Contracts.Configuration;
using Panel.SharedKernel;

namespace Panel.Contracts.Http;

/// <summary>Тело <c>POST /api/config/preview</c> и <c>PUT /api/config</c>.</summary>
public sealed record ConfigRequest
{
	[JsonPropertyName("config")] public JsonNode? Config { get; init; }

	/// <summary>Ревизия, полученная при чтении. Пустая означает «не проверять».</summary>
	[JsonPropertyName("revision")] public string? Revision { get; init; }
}

/// <summary>Тело <c>POST /api/world-config/runtime</c>.</summary>
public sealed record LiveWorldConfigRequest
{
	[JsonPropertyName("action")] public string Action { get; init; } = string.Empty;
	[JsonPropertyName("datatype")] public string DataType { get; init; } = "string";
	[JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
	[JsonPropertyName("value")] public string Value { get; init; } = string.Empty;
}

/// <summary>Ответ <c>GET /api/config</c>: документ, ревизия, схема формы, состояние применения, копии.</summary>
public sealed record ConfigDescriptionResponse
{
	[JsonPropertyName("config")] public JsonObject Config { get; init; } = [];
	[JsonPropertyName("revision")] public string Revision { get; init; } = string.Empty;
	[JsonPropertyName("validation_warning")] public Phrase? ValidationWarning { get; init; }
	[JsonPropertyName("schema")] public ConfigSchemaResponse Schema { get; init; } = new();
	[JsonPropertyName("apply_state")] public ApplyStateResponse ApplyState { get; init; } = new();
	[JsonPropertyName("backups")] public IReadOnlyList<ConfigBackupPayload> Backups { get; init; } = [];
}

public sealed record ConfigSchemaResponse
{
	[JsonPropertyName("categories")] public IReadOnlyList<ConfigCategoryResponse> Categories { get; init; } = [];
	[JsonPropertyName("fields")] public IReadOnlyList<ConfigFieldResponse> Fields { get; init; } = [];
}

public sealed record ConfigCategoryResponse
{
	[JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
	[JsonPropertyName("label")] public Phrase Label { get; init; } = Phrase.From(string.Empty);
}

/// <summary>Поле схемы.</summary>
public sealed record ConfigFieldResponse
{
	[JsonPropertyName("key")] public string Key { get; init; } = string.Empty;
	[JsonPropertyName("label")] public Phrase Label { get; init; } = Phrase.From(string.Empty);
	[JsonPropertyName("category")] public string Category { get; init; } = string.Empty;
	[JsonPropertyName("type")] public string Type { get; init; } = string.Empty;
	[JsonPropertyName("description")] public Phrase Description { get; init; } = Phrase.From(string.Empty);
	[JsonPropertyName("min")] public double? Min { get; init; }
	[JsonPropertyName("max")] public double? Max { get; init; }
	[JsonPropertyName("options")] public IReadOnlyList<ConfigOptionResponse> Options { get; init; } = [];
	[JsonPropertyName("restart_required")] public bool RestartRequired { get; init; }
	[JsonPropertyName("nullable")] public bool Nullable { get; init; }
	[JsonPropertyName("maximum_length")] public int? MaximumLength { get; init; }
}

public sealed record ConfigOptionResponse
{
	[JsonPropertyName("value")] public long Value { get; init; }
	[JsonPropertyName("label")] public Phrase Label { get; init; } = Phrase.From(string.Empty);
}

/// <summary>Состояние применения: <c>ConfigApplyTracker.inspect()</c>.</summary>
public sealed record ApplyStateResponse
{
	[JsonPropertyName("state")] public string State { get; init; } = "applied";
	[JsonPropertyName("active")] public bool Active { get; init; }
	[JsonPropertyName("restart_required")] public bool RestartRequired { get; init; }
	[JsonPropertyName("changed_keys")] public IReadOnlyList<string> ChangedKeys { get; init; } = [];
	[JsonPropertyName("saved_at")] public long? SavedAt { get; init; }
	[JsonPropertyName("source")] public string Source { get; init; } = "service";
}

/// <summary>Строка предпросмотра. <c>before</c> и <c>after</c> — значения как есть, пароль скрыт.</summary>
public sealed record ConfigChangeResponse
{
	[JsonPropertyName("key")] public string Key { get; init; } = string.Empty;
	[JsonPropertyName("label")] public Phrase Label { get; init; } = Phrase.From(string.Empty);
	[JsonPropertyName("before")] public JsonNode? Before { get; init; }
	[JsonPropertyName("after")] public JsonNode? After { get; init; }
	[JsonPropertyName("restart_required")] public bool RestartRequired { get; init; }
}

public sealed record ConfigPreviewResponse
{
	[JsonPropertyName("changes")] public IReadOnlyList<ConfigChangeResponse> Changes { get; init; } = [];
	[JsonPropertyName("restart_required")] public bool RestartRequired { get; init; }
}

/// <summary>
/// Ответ <c>PUT /api/config</c>. <c>auto_restarted</c> есть всегда, даже если изменений нет.
/// </summary>
public sealed record ConfigSaveResponse
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;
	[JsonPropertyName("backup")] public string? Backup { get; init; }
	[JsonPropertyName("restart_required")] public bool RestartRequired { get; init; }
	[JsonPropertyName("auto_restarted")] public bool AutoRestarted { get; init; }
	[JsonPropertyName("firewall_warning")] public Phrase? FirewallWarning { get; init; }
	[JsonPropertyName("apply_state")] public ApplyStateResponse ApplyState { get; init; } = new();
}

public sealed record ConfigRollbackResponse
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;
	[JsonPropertyName("restored")] public string Restored { get; init; } = string.Empty;
	[JsonPropertyName("safety_backup")] public string? SafetyBackup { get; init; }
	[JsonPropertyName("restart_required")] public bool RestartRequired { get; init; } = true;
	[JsonPropertyName("auto_restarted")] public bool AutoRestarted { get; init; }
	[JsonPropertyName("firewall_warning")] public Phrase? FirewallWarning { get; init; }
	[JsonPropertyName("apply_state")] public ApplyStateResponse ApplyState { get; init; } = new();
}

/// <summary>
/// Ответ <c>GET /api/world-config</c>. Ревизия — время изменения файла в наносекундах, а не
/// хеш.
/// </summary>
public sealed record WorldTemplateResponse
{
	[JsonPropertyName("config")] public JsonObject Config { get; init; } = [];
	[JsonPropertyName("revision")] public long Revision { get; init; }
}

/// <summary>Ответ <c>GET /api/world-config/runtime</c>: живой <c>World.Config</c> из телеметрии.</summary>
public sealed record LiveWorldConfigResponse
{
	[JsonPropertyName("available")] public bool Available { get; init; }
	[JsonPropertyName("configuration")] public JsonObject Configuration { get; init; } = [];
	[JsonPropertyName("detail")] public Phrase? Detail { get; init; }
}

public sealed record LiveWorldConfigChangeResponse
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;
	[JsonPropertyName("action")] public string Action { get; init; } = string.Empty;
	[JsonPropertyName("datatype")] public string DataType { get; init; } = string.Empty;
	[JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
}
