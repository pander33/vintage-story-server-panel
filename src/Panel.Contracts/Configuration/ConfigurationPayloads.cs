using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Panel.SharedKernel;

namespace Panel.Contracts.Configuration;

/// <summary>
/// Ответ демона на <c>config-get</c>: документ и время изменения файла.
/// </summary>
public sealed record ServerConfigFilePayload
{
	[JsonPropertyName("config")] public JsonObject Config { get; init; } = [];

	/// <summary>
	/// Время изменения файла в наносекундах. В .NET точность 100 нс (тики <c>FileInfo</c>); метка
	/// служит ревизией шаблона мира и сравнивается только сама с собой, поэтому потеря младших
	/// разрядов не мешает.
	/// </summary>
	[JsonPropertyName("modified_ns")] public long ModifiedNanoseconds { get; init; }
}

public sealed record ConfigBackupPayload
{
	[JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
	[JsonPropertyName("size")] public long Size { get; init; }
	[JsonPropertyName("modified")] public long Modified { get; init; }
}

public sealed record ConfigBackupsPayload
{
	[JsonPropertyName("backups")] public IReadOnlyList<ConfigBackupPayload> Backups { get; init; } = [];
}

/// <summary>Ответ <c>config-put</c>. <c>backup</c> — полный путь.</summary>
public sealed record ConfigWritePayload
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;
	[JsonPropertyName("backup")] public string? Backup { get; init; }
	[JsonPropertyName("restart_required")] public bool RestartRequired { get; init; } = true;
}

/// <summary>Ответ <c>config-rollback</c>. <c>safety_backup</c> — имя, а не путь.</summary>
public sealed record ConfigRollbackPayload
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;
	[JsonPropertyName("restored")] public string Restored { get; init; } = string.Empty;
	[JsonPropertyName("safety_backup")] public string? SafetyBackup { get; init; }
	[JsonPropertyName("restart_required")] public bool RestartRequired { get; init; } = true;
}

/// <summary>Один стиль игры из ответа <c>world-config-schema</c>, с готовыми значениями параметров.</summary>
public sealed record WorldPlaystylePayload
{
	[JsonPropertyName("code")] public string Code { get; init; } = string.Empty;
	[JsonPropertyName("label")] public string Label { get; init; } = string.Empty;
	[JsonPropertyName("description")] public string Description { get; init; } = string.Empty;
	[JsonPropertyName("playlist_code")] public string PlaylistCode { get; init; } = string.Empty;
	[JsonPropertyName("world_type")] public string WorldType { get; init; } = string.Empty;
	[JsonPropertyName("mods")] public IReadOnlyList<string> Mods { get; init; } = [];
	[JsonPropertyName("values")] public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();
}

/// <summary>Допустимое значение параметра выбора: код и подпись.</summary>
public sealed record WorldAttributeOptionPayload
{
	[JsonPropertyName("value")] public string Value { get; init; } = string.Empty;
	[JsonPropertyName("label")] public string Label { get; init; } = string.Empty;
}

/// <summary>Один параметр мира из ответа <c>world-config-schema</c>.</summary>
public sealed record WorldAttributePayload
{
	[JsonPropertyName("code")] public string Code { get; init; } = string.Empty;
	[JsonPropertyName("label")] public string Label { get; init; } = string.Empty;
	[JsonPropertyName("description")] public string Description { get; init; } = string.Empty;

	/// <summary>Одно из: <c>bool</c>, <c>dropdown</c>, <c>integer</c>, <c>number</c>, <c>text</c>, <c>unknown</c>.</summary>
	[JsonPropertyName("data_type")] public string DataType { get; init; } = "unknown";

	[JsonPropertyName("category")] public string Category { get; init; } = string.Empty;
	[JsonPropertyName("options")] public IReadOnlyList<WorldAttributeOptionPayload> Options { get; init; } = [];
	[JsonPropertyName("default")] public string? Default { get; init; }
	[JsonPropertyName("only_during_world_create")] public bool OnlyDuringWorldCreate { get; init; }
}

/// <summary>Раздел параметров мира: код и подпись из языковых файлов игры.</summary>
public sealed record WorldCategoryPayload
{
	[JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
	[JsonPropertyName("label")] public string Label { get; init; } = string.Empty;
}

/// <summary>
/// Ответ демона на <c>world-config-schema</c>: параметры мира, объявленные игрой и включёнными
/// модами. Подписи уже разрешены на языке запроса (русский → английский или английский → данные
/// игры → сам код), панели искать их не нужно.
/// </summary>
public sealed record WorldSchemaPayload
{
	[JsonPropertyName("playstyles")] public IReadOnlyList<WorldPlaystylePayload> Playstyles { get; init; } = [];
	[JsonPropertyName("categories")] public IReadOnlyList<WorldCategoryPayload> Categories { get; init; } = [];
	[JsonPropertyName("attributes")] public IReadOnlyList<WorldAttributePayload> Attributes { get; init; } = [];
	[JsonPropertyName("warnings")] public IReadOnlyList<Phrase> Warnings { get; init; } = [];
}
