using System.Text.Json.Serialization;
using Panel.SharedKernel;

namespace Panel.Contracts.Content;

/// <summary>
/// <c>modpack.json</c> — опись сборки модов: какие моды каких версий стояли на сервере и какие
/// файлы настроек лежат рядом в <c>config/</c>.
/// </summary>
public sealed record ModPackManifestPayload
{
	[JsonPropertyName("format")]
	public required string Format { get; init; }

	[JsonPropertyName("version")]
	public required int Version { get; init; }

	[JsonPropertyName("exported_at")]
	public required DateTimeOffset ExportedAt { get; init; }

	/// <summary>Сервер, с которого снята сборка; для показа.</summary>
	[JsonPropertyName("server")]
	public string Server { get; init; } = string.Empty;

	[JsonPropertyName("game_version")]
	public string? GameVersion { get; init; }

	[JsonPropertyName("mods")]
	public IReadOnlyList<ModPackModPayload> Mods { get; init; } = [];

	/// <summary>Имена файлов в <c>config/</c>; каждое совпадает с файлом архива.</summary>
	[JsonPropertyName("configs")]
	public IReadOnlyList<string> Configs { get; init; } = [];

	/// <summary>Файлы настроек, которые не удалось положить в сборку, и почему.</summary>
	[JsonPropertyName("skipped_configs")]
	public IReadOnlyList<ModPackSkippedConfigPayload> SkippedConfigs { get; init; } = [];
}

/// <summary>Мод в описи сборки.</summary>
public sealed record ModPackModPayload
{
	[JsonPropertyName("modid")]
	public required string ModId { get; init; }

	[JsonPropertyName("version")]
	public string Version { get; init; } = string.Empty;

	[JsonPropertyName("enabled")]
	public bool Enabled { get; init; } = true;

	[JsonPropertyName("display_name")]
	public string? DisplayName { get; init; }

	[JsonPropertyName("side")]
	public string? Side { get; init; }

	/// <summary>Имя архива на исходном сервере; для показа, установка идёт из ModDB.</summary>
	[JsonPropertyName("file")]
	public string? File { get; init; }
}

/// <summary>Файл настроек, не попавший в сборку.</summary>
public sealed record ModPackSkippedConfigPayload
{
	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[JsonPropertyName("reason")]
	public required Phrase Reason { get; init; }
}

/// <summary>Ответ <c>POST /api/mods/pack/inspect</c>: что изменит сборка на этом сервере.</summary>
public sealed record ModPackPlanPayload
{
	[JsonPropertyName("server")]
	public string Server { get; init; } = string.Empty;

	[JsonPropertyName("exported_at")]
	public DateTimeOffset ExportedAt { get; init; }

	[JsonPropertyName("game_version")]
	public string? GameVersion { get; init; }

	/// <summary>Версия игры этого сервера: с ней ModDB сверяет совместимость при установке.</summary>
	[JsonPropertyName("current_game_version")]
	public string? CurrentGameVersion { get; init; }

	[JsonPropertyName("mods")]
	public IReadOnlyList<ModPackModPlanPayload> Mods { get; init; } = [];

	/// <summary>Моды этого сервера, которых нет в сборке. Панель их не удаляет.</summary>
	[JsonPropertyName("extra")]
	public IReadOnlyList<ModPackExtraPayload> Extra { get; init; } = [];

	[JsonPropertyName("configs")]
	public IReadOnlyList<ModPackConfigPlanPayload> Configs { get; init; } = [];

	[JsonPropertyName("skipped_configs")]
	public IReadOnlyList<ModPackSkippedConfigPayload> SkippedConfigs { get; init; } = [];
}

/// <summary>Мод сборки против установленного.</summary>
public sealed record ModPackModPlanPayload
{
	[JsonPropertyName("modid")]
	public required string ModId { get; init; }

	[JsonPropertyName("display_name")]
	public string? DisplayName { get; init; }

	/// <summary>Версия в сборке.</summary>
	[JsonPropertyName("version")]
	public string Version { get; init; } = string.Empty;

	/// <summary>Включён ли мод в сборке.</summary>
	[JsonPropertyName("enabled")]
	public bool Enabled { get; init; }

	/// <summary><c>missing</c>, <c>different</c>, <c>same</c> или <c>ambiguous</c> (на сервере несколько архивов с этим modid).</summary>
	[JsonPropertyName("status")]
	public required string Status { get; init; }

	/// <summary>Установленная версия; <c>null</c>, если мода нет.</summary>
	[JsonPropertyName("installed")]
	public string? Installed { get; init; }

	[JsonPropertyName("installed_enabled")]
	public bool? InstalledEnabled { get; init; }

	/// <summary>Архив на этом сервере, если он один.</summary>
	[JsonPropertyName("archive")]
	public string? Archive { get; init; }

	/// <summary>Включение архива расходится со сборкой, и его можно переключить.</summary>
	[JsonPropertyName("toggle")]
	public bool Toggle { get; init; }
}

/// <summary>Мод сервера, которого нет в сборке.</summary>
public sealed record ModPackExtraPayload
{
	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[JsonPropertyName("modid")]
	public string? ModId { get; init; }

	[JsonPropertyName("display_name")]
	public string? DisplayName { get; init; }

	[JsonPropertyName("enabled")]
	public bool Enabled { get; init; }
}

/// <summary>Файл настроек сборки против файла сервера.</summary>
public sealed record ModPackConfigPlanPayload
{
	[JsonPropertyName("name")]
	public required string Name { get; init; }

	/// <summary><c>new</c>, <c>changed</c> или <c>same</c>.</summary>
	[JsonPropertyName("status")]
	public required string Status { get; init; }
}

/// <summary>Ответ <c>POST /api/mods/pack/apply</c>.</summary>
public sealed record ModPackApplyPayload
{
	[JsonPropertyName("ok")]
	public bool Ok { get; init; } = true;

	[JsonPropertyName("configs")]
	public IReadOnlyList<string> Configs { get; init; } = [];

	[JsonPropertyName("toggled")]
	public IReadOnlyList<string> Toggled { get; init; } = [];

	[JsonPropertyName("failures")]
	public IReadOnlyList<ModPackFailurePayload> Failures { get; init; } = [];

	[JsonPropertyName("restart_required")]
	public bool RestartRequired { get; init; }
}

/// <summary>Шаг применения, который не удался; остальные шаги выполнены.</summary>
public sealed record ModPackFailurePayload
{
	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[JsonPropertyName("error")]
	public required Phrase Error { get; init; }
}
