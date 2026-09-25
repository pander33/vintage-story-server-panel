using System.Text.Json.Serialization;

namespace Panel.Contracts.Http;

/// <summary>Тело запроса с одним именем.</summary>
public sealed record NameRequest
{
	[JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
}

/// <summary>
/// Тело запроса с подтверждением. Форма заморожена: фронтенд посылает именно эти два поля.
/// </summary>
public sealed record ConfirmedNameRequest
{
	[JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
	[JsonPropertyName("confirmation")] public string Confirmation { get; init; } = string.Empty;
}

public sealed record ModRollbackRequest
{
	[JsonPropertyName("modid")] public string ModId { get; init; } = string.Empty;
	[JsonPropertyName("archive")] public string Archive { get; init; } = string.Empty;
}

public sealed record WorldCloneRequest
{
	[JsonPropertyName("source")] public string Source { get; init; } = string.Empty;
	[JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
}

/// <summary>Действие над миром: <c>activate</c>, <c>archive</c> или <c>trash</c>.</summary>
public sealed record WorldActionRequest
{
	[JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
	[JsonPropertyName("action")] public string Action { get; init; } = string.Empty;
	[JsonPropertyName("confirmation")] public string Confirmation { get; init; } = string.Empty;
}

public sealed record WorldCreateRequest
{
	[JsonPropertyName("filename")] public string FileName { get; init; } = string.Empty;
	[JsonPropertyName("world_name")] public string WorldName { get; init; } = string.Empty;
	[JsonPropertyName("seed")] public string Seed { get; init; } = string.Empty;
	[JsonPropertyName("playstyle")] public string PlayStyle { get; init; } = "surviveandbuild";
}

/// <summary>Возврат мира из архива или корзины.</summary>
public sealed record WorldRestoreRequest
{
	[JsonPropertyName("mode")] public string Mode { get; init; } = string.Empty;
	[JsonPropertyName("stored_name")] public string StoredName { get; init; } = string.Empty;
	[JsonPropertyName("target_name")] public string TargetName { get; init; } = string.Empty;
	[JsonPropertyName("confirmation")] public string Confirmation { get; init; } = string.Empty;
}

/// <summary>Безвозвратное удаление мира из архива или корзины.</summary>
public sealed record ArchivedWorldRequest
{
	[JsonPropertyName("mode")] public string Mode { get; init; } = string.Empty;
	[JsonPropertyName("stored_name")] public string StoredName { get; init; } = string.Empty;
	[JsonPropertyName("confirmation")] public string Confirmation { get; init; } = string.Empty;
}

/// <summary>Извлечение одного мира из резервной копии под новым именем.</summary>
public sealed record BackupWorldRestoreRequest
{
	[JsonPropertyName("backup")] public string Backup { get; init; } = string.Empty;
	[JsonPropertyName("world")] public string World { get; init; } = string.Empty;
	[JsonPropertyName("target_name")] public string TargetName { get; init; } = string.Empty;
	[JsonPropertyName("confirmation")] public string Confirmation { get; init; } = string.Empty;
}

/// <summary>
/// Настройка политики удержания копий. Режим задаётся логическим <c>apply</c>, а не строкой.
/// </summary>
public sealed record RetentionRequest
{
	[JsonPropertyName("keep_count")] public int KeepCount { get; init; } = 10;
	[JsonPropertyName("keep_days")] public int KeepDays { get; init; } = 30;
	[JsonPropertyName("minimum_free_gb")] public int MinimumFreeGigabytes { get; init; }
	[JsonPropertyName("apply")] public bool Apply { get; init; }
	[JsonPropertyName("confirmation")] public string Confirmation { get; init; } = string.Empty;
}

/// <summary>Сохранение файла настроек мода. Имя файла передаётся в адресе, не в теле.</summary>
public sealed record JsonConfigRequest
{
	/// <summary>Содержимое целиком, как его собрал редактор.</summary>
	[JsonPropertyName("config")] public System.Text.Json.Nodes.JsonNode? Config { get; init; }

	/// <summary>
	/// Метка, полученная при чтении. Ноль означает «не проверять». При несовпадении 409: файл
	/// правили в обход панели, и сохранение перезаписало бы эту правку.
	/// </summary>
	[JsonPropertyName("revision")] public long Revision { get; init; }
}

/// <summary>Тело <c>POST /api/history/delete</c>.</summary>
public sealed record HistoryDeleteRequest
{
	[JsonPropertyName("store")] public string Store { get; init; } = string.Empty;
	[JsonPropertyName("key")] public string Key { get; init; } = string.Empty;
}

/// <summary>Тело <c>POST /api/history/prune</c>. По умолчанию <c>keep</c> равно 5.</summary>
public sealed record HistoryPruneRequest
{
	[JsonPropertyName("store")] public string Store { get; init; } = string.Empty;
	[JsonPropertyName("keep")] public int Keep { get; init; } = 5;
	[JsonPropertyName("confirmation")] public string Confirmation { get; init; } = string.Empty;
}
