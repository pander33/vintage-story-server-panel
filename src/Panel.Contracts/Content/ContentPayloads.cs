using System.Text.Json.Serialization;
using Panel.SharedKernel;

namespace Panel.Contracts.Content;

/// <summary>
/// Один мод в ответе <c>GET /api/mods</c>. Имена полей заморожены (ADR-16): фронтенд читает
/// именно их.
/// </summary>
public sealed record ModEntryPayload
{
	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[JsonPropertyName("enabled")]
	public required bool Enabled { get; init; }

	[JsonPropertyName("size")]
	public required long Size { get; init; }

	[JsonPropertyName("modified")]
	public required long Modified { get; init; }

	/// <summary>null, если <c>modinfo.json</c> не прочитан.</summary>
	[JsonPropertyName("modid")]
	public string? ModId { get; init; }

	[JsonPropertyName("version")]
	public string? Version { get; init; }

	[JsonPropertyName("display_name")]
	public string? DisplayName { get; init; }

	[JsonPropertyName("side")]
	public string? Side { get; init; }

	[JsonPropertyName("dependencies")]
	public IReadOnlyDictionary<string, string> Dependencies { get; init; } =
		new Dictionary<string, string>();

	[JsonPropertyName("missing_dependencies")]
	public IReadOnlyList<string> MissingDependencies { get; init; } = [];

	/// <summary>
	/// Оценка зависимостей с учётом версий. Её делает панель, а не демон, потому что нужна версия
	/// игры. Демон поле не заполняет и в ответ не пишет.
	/// </summary>
	[JsonPropertyName("dependency_issues")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public IReadOnlyList<DependencyIssuePayload>? DependencyIssues { get; init; }

	/// <summary>Текст ошибки разбора архива; показывается пользователю как есть.</summary>
	[JsonPropertyName("error")]
	public Phrase? Error { get; init; }
}

/// <summary>Элемент <c>dependency_issues</c>.</summary>
public sealed record DependencyIssuePayload
{
	[JsonPropertyName("modid")]
	public required string ModId { get; init; }

	/// <summary><c>missing</c>, <c>outdated</c> или <c>unverified</c>.</summary>
	[JsonPropertyName("status")]
	public required string Status { get; init; }

	[JsonPropertyName("required")]
	public required string Required { get; init; }

	/// <summary>У отсутствующей зависимости <c>null</c>, и поле пишется.</summary>
	[JsonPropertyName("installed")]
	public string? Installed { get; init; }

	/// <summary>Только у <c>unverified</c>: <c>invalid_requirement</c> или <c>invalid_installed_version</c>.</summary>
	[JsonPropertyName("reason")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Reason { get; init; }
}

/// <summary>Ответ <c>GET /api/mods</c>.</summary>
public sealed record ModsPayload
{
	[JsonPropertyName("mods")]
	public IReadOnlyList<ModEntryPayload> Mods { get; init; } = [];

	[JsonPropertyName("enabled")]
	public int Enabled { get; init; }

	[JsonPropertyName("duplicate_ids")]
	public IReadOnlyList<string> DuplicateIds { get; init; } = [];

	/// <summary>Идентификатор мода → имена сохранённых версий для отката.</summary>
	[JsonPropertyName("rollbacks")]
	public IReadOnlyDictionary<string, IReadOnlyList<string>> Rollbacks { get; init; } =
		new Dictionary<string, IReadOnlyList<string>>();

	[JsonPropertyName("profiles")]
	public IReadOnlyList<string> Profiles { get; init; } = [];
}

/// <summary>Ответ установки, отката и переключения мода.</summary>
public sealed record ModChangePayload
{
	[JsonPropertyName("ok")]
	public bool Ok { get; init; } = true;

	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[JsonPropertyName("modid")]
	public string? ModId { get; init; }

	[JsonPropertyName("version")]
	public string? Version { get; init; }

	[JsonPropertyName("enabled")]
	public bool? Enabled { get; init; }

	[JsonPropertyName("archived")]
	public IReadOnlyList<string> Archived { get; init; } = [];

	/// <summary>Изменение вступит в силу после перезапуска: моды загружаются при старте.</summary>
	[JsonPropertyName("restart_required")]
	public bool RestartRequired { get; init; } = true;
}

/// <summary>Ответ сохранения, применения и удаления профиля модов.</summary>
public sealed record ModProfilePayload
{
	[JsonPropertyName("ok")]
	public bool Ok { get; init; } = true;

	[JsonPropertyName("profile")]
	public required string Profile { get; init; }

	[JsonPropertyName("mods")]
	public IReadOnlyList<string> Mods { get; init; } = [];

	[JsonPropertyName("remaining")]
	public IReadOnlyList<string> Remaining { get; init; } = [];

	[JsonPropertyName("restart_required")]
	public bool RestartRequired { get; init; } = true;
}

/// <summary>Один мир в ответе <c>GET /api/worlds</c>.</summary>
public sealed record WorldEntryPayload
{
	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[JsonPropertyName("size")]
	public required long Size { get; init; }

	[JsonPropertyName("modified")]
	public required long Modified { get; init; }

	[JsonPropertyName("active")]
	public required bool Active { get; init; }
}

/// <summary>Ответ <c>GET /api/worlds</c>.</summary>
public sealed record WorldsPayload
{
	[JsonPropertyName("worlds")]
	public IReadOnlyList<WorldEntryPayload> Worlds { get; init; } = [];

	/// <summary>Имя файла активного мира; null, если настройки не прочитаны.</summary>
	[JsonPropertyName("active")]
	public string? Active { get; init; }

	[JsonPropertyName("server_active")]
	public bool ServerActive { get; init; }
}

/// <summary>Убранный мир: <c>.archive</c> или <c>.trash</c>.</summary>
public sealed record StoredWorldPayload
{
	[JsonPropertyName("stored_name")]
	public required string StoredName { get; init; }

	[JsonPropertyName("original_name")]
	public required string OriginalName { get; init; }

	[JsonPropertyName("mode")]
	public required string Mode { get; init; }

	[JsonPropertyName("size")]
	public required long Size { get; init; }

	[JsonPropertyName("modified")]
	public required long Modified { get; init; }
}

/// <summary>Ответ действия над миром.</summary>
public sealed record WorldChangePayload
{
	[JsonPropertyName("ok")]
	public bool Ok { get; init; } = true;

	[JsonPropertyName("name")]
	public string? Name { get; init; }

	[JsonPropertyName("active")]
	public string? Active { get; init; }

	[JsonPropertyName("mode")]
	public string? Mode { get; init; }

	[JsonPropertyName("stored_as")]
	public string? StoredAs { get; init; }

	[JsonPropertyName("size")]
	public long? Size { get; init; }

	[JsonPropertyName("freed")]
	public long? Freed { get; init; }

	/// <summary>Файл базы создаст игра при первом запуске, панель его не создаёт.</summary>
	[JsonPropertyName("pending_creation")]
	public bool? PendingCreation { get; init; }

	[JsonPropertyName("restart_required")]
	public bool RestartRequired { get; init; } = true;
}

/// <summary>Одна резервная копия.</summary>
public sealed record BackupEntryPayload
{
	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[JsonPropertyName("size")]
	public required long Size { get; init; }

	[JsonPropertyName("modified")]
	public required long Modified { get; init; }
}

/// <summary>Ответ <c>GET /api/backups</c>.</summary>
public sealed record BackupsPayload
{
	[JsonPropertyName("backups")]
	public IReadOnlyList<BackupEntryPayload> Backups { get; init; } = [];
}

/// <summary>Содержимое копии, полученное чтением всего архива.</summary>
public sealed record BackupContentsPayload
{
	[JsonPropertyName("ok")]
	public bool Ok { get; init; } = true;

	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[JsonPropertyName("size")]
	public long Size { get; init; }

	[JsonPropertyName("files")]
	public int Files { get; init; }

	[JsonPropertyName("unpacked_size")]
	public long UnpackedSize { get; init; }

	[JsonPropertyName("worlds")]
	public IReadOnlyList<BackupWorldPayload> Worlds { get; init; } = [];
}

/// <summary>Мир внутри резервной копии.</summary>
public sealed record BackupWorldPayload
{
	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[JsonPropertyName("member")]
	public required string Member { get; init; }

	[JsonPropertyName("size")]
	public long Size { get; init; }
}

/// <summary>
/// Ход долгой операции над сервером: копия, восстановление, выгрузка пакета. Пока ничего не
/// идёт, <c>active</c> ложно и остальное пусто.
/// </summary>
public sealed record OperationPayload
{
	[JsonPropertyName("active")]
	public bool Active { get; init; }

	/// <summary>Что делается, кодом: имя операции демона либо <c>backup</c>, <c>restore</c>, <c>export</c>.</summary>
	[JsonPropertyName("kind")]
	public string Kind { get; init; } = string.Empty;

	/// <summary>То же готовым названием для показа.</summary>
	[JsonPropertyName("title")]
	public Phrase? Title { get; init; }

	/// <summary>Все стадии по порядку, готовые названия для показа.</summary>
	[JsonPropertyName("stages")]
	public IReadOnlyList<Phrase> Stages { get; init; } = [];

	/// <summary>Номер идущей стадии в <see cref="Stages"/>, с нуля.</summary>
	[JsonPropertyName("stage")]
	public int Stage { get; init; }

	[JsonPropertyName("elapsed_seconds")]
	public double ElapsedSeconds { get; init; }

	/// <summary>Сколько байт обработано на этой стадии; пусто, если стадия байтов не считает.</summary>
	[JsonPropertyName("done_bytes")]
	public long? DoneBytes { get; init; }

	[JsonPropertyName("total_bytes")]
	public long? TotalBytes { get; init; }
}

/// <summary>Ответ создания, восстановления и удаления копии.</summary>
public sealed record BackupChangePayload
{
	[JsonPropertyName("ok")]
	public bool Ok { get; init; } = true;

	[JsonPropertyName("name")]
	public string? Name { get; init; }

	[JsonPropertyName("size")]
	public long? Size { get; init; }

	[JsonPropertyName("restored")]
	public string? Restored { get; init; }

	[JsonPropertyName("safety_backup")]
	public string? SafetyBackup { get; init; }

	[JsonPropertyName("freed")]
	public long? Freed { get; init; }

	[JsonPropertyName("remaining")]
	public int? Remaining { get; init; }
}

/// <summary>Ответ политики удержания: и предпросмотр, и выполнение.</summary>
public sealed record RetentionPayload
{
	[JsonPropertyName("ok")]
	public bool Ok { get; init; } = true;

	/// <summary><c>preview</c> или <c>apply</c>.</summary>
	[JsonPropertyName("mode")]
	public required string Mode { get; init; }

	[JsonPropertyName("candidates")]
	public IReadOnlyList<string> Candidates { get; init; } = [];

	[JsonPropertyName("removed")]
	public IReadOnlyList<string> Removed { get; init; } = [];

	[JsonPropertyName("bytes")]
	public long Bytes { get; init; }

	[JsonPropertyName("free_before")]
	public long FreeBefore { get; init; }

	[JsonPropertyName("free_after")]
	public long FreeAfter { get; init; }

	[JsonPropertyName("target_free")]
	public long TargetFree { get; init; }

	[JsonPropertyName("target_met")]
	public bool TargetMet { get; init; }
}

/// <summary>Перечень файлов настроек модов.</summary>
public sealed record ModConfigsPayload
{
	[JsonPropertyName("files")]
	public IReadOnlyList<ModConfigEntryPayload> Files { get; init; } = [];
}

/// <summary>Один файл настроек мода.</summary>
public sealed record ModConfigEntryPayload
{
	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[JsonPropertyName("size")]
	public long Size { get; init; }

	[JsonPropertyName("modified")]
	public long Modified { get; init; }
}

/// <summary>Содержимое одного файла настроек мода.</summary>
public sealed record ModConfigPayload
{
	[JsonPropertyName("name")]
	public required string Name { get; init; }

	/// <summary>Разобранный JSON как есть; редактор показывает его целиком.</summary>
	[JsonPropertyName("config")]
	public System.Text.Json.Nodes.JsonNode? Config { get; init; }

	/// <summary>
	/// Метка изменения, чтобы редактор заметил правку файла в обход панели и не перезаписал её.
	/// </summary>
	[JsonPropertyName("modified_ns")]
	public long ModifiedNanoseconds { get; init; }
}

/// <summary>Мир, выгруженный для скачивания.</summary>
public sealed record WorldExportPayload
{
	[JsonPropertyName("ok")]
	public bool Ok { get; init; } = true;

	[JsonPropertyName("name")]
	public required string Name { get; init; }

	/// <summary>Имя файла в каталоге промежуточного хранения, не путь.</summary>
	[JsonPropertyName("staged")]
	public required string Staged { get; init; }

	[JsonPropertyName("size")]
	public long Size { get; init; }
}
