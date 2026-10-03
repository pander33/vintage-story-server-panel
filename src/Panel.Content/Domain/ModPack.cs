using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Contracts;
using Panel.Contracts.Content;
using Panel.SharedKernel;

namespace Panel.Content.Domain;

/// <summary>Что лежит в записи сборки.</summary>
public enum ModPackEntryKind
{
	Manifest,
	Config,
	Directory,
}

/// <summary>Запись сборки после суждения: опись, файл настроек или каталог <c>config/</c>.</summary>
public readonly record struct ModPackEntry(ModPackEntryKind Kind, ModConfigName Config);

/// <summary>
/// Сборка модов — ZIP с описью <c>modpack.json</c> и файлами настроек в <c>config/</c>.
/// Самих модов в ней нет: их ставит ModDB, по одному и с подтверждением.
/// </summary>
/// <remarks>
/// Сборка приходит от другого человека, поэтому суждение строгое: другие файлы, ссылки и
/// вложенные каталоги — отказ, а не пропуск. Содержимое читается в память панели, на диск
/// демона попадают только файлы настроек, и каждый проходит ту же запись, что и правка в редакторе.
/// </remarks>
public static class ModPack
{
	public const string ManifestName = "modpack.json";
	public const string ConfigDirectory = "config";
	public const string Format = "vs-panel-modpack";
	public const int FormatVersion = 1;

	public const int MaximumEntries = 2_000;
	public const int MaximumMods = 1_000;
	public const int MaximumPackMegabytes = 32;
	public const long MaximumManifestBytes = 1024 * 1024;

	/// <summary>Столько же читает редактор настроек мода.</summary>
	public const long MaximumConfigBytes = 2 * 1024 * 1024;

	public const long MaximumUnpackedBytes = 64L * 1024 * 1024;

	private const int maximumVersionLength = 64;

	public static Result<ModPackEntry> JudgeEntry(ArchiveEntry entry)
	{
		var judged = ArchiveSafety.Judge(entry);
		if (!judged.IsSuccess)
		{
			return Result.Fail<ModPackEntry>(judged.Failure!);
		}

		var path = judged.Value.RelativePath;
		if (judged.Value.Kind == ArchiveEntryKind.Directory)
		{
			return path == ConfigDirectory
				? new ModPackEntry(ModPackEntryKind.Directory, default)
				: Failure.Invalid($"Лишний каталог в сборке: {path}");
		}

		if (judged.Value.Kind != ArchiveEntryKind.File)
		{
			return Failure.Invalid($"Ссылки в сборке запрещены: {path}");
		}

		if (path == ManifestName)
		{
			return new ModPackEntry(ModPackEntryKind.Manifest, default);
		}

		var prefix = ConfigDirectory + "/";
		if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.IndexOf('/', prefix.Length) >= 0)
		{
			return Failure.Invalid($"Лишний файл в сборке: {path}");
		}

		var name = ModConfigName.Create(path[prefix.Length..]);
		return name.IsSuccess
			? new ModPackEntry(ModPackEntryKind.Config, name.Value)
			: Failure.Invalid($"Недопустимое имя файла настроек в сборке: {path}");
	}

	/// <summary>Размер записи до чтения содержимого: по объявленной длине, потом по прочитанной.</summary>
	public static Result<long> CountBytes(long total, long length, ModPackEntryKind kind)
	{
		var limit = kind == ModPackEntryKind.Manifest ? MaximumManifestBytes : MaximumConfigBytes;
		if (length < 0 || length > limit)
		{
			return kind == ModPackEntryKind.Manifest
				? Failure.Invalid("Опись сборки больше 1 МБ")
				: Failure.Invalid("Файл настроек в сборке больше 2 МБ");
		}

		return total > MaximumUnpackedBytes - length
			? Failure.Invalid("Содержимое сборки больше 64 МБ")
			: total + length;
	}

	/// <summary>Опись сборки из <c>modpack.json</c>: формат, версия, моды и имена настроек.</summary>
	public static Result<ModPackManifestPayload> FromJson(JsonNode? node)
	{
		if (node is not JsonObject document || document["format"]?.GetValueKind() != JsonValueKind.String
			|| document["format"]!.GetValue<string>() != Format)
		{
			return Failure.Invalid("Это не сборка модов панели: в modpack.json нет её формата");
		}

		if (document["version"] is not JsonValue version || !version.TryGetValue<int>(out var number))
		{
			return Failure.Invalid("В описи сборки нет версии формата");
		}

		if (number != FormatVersion)
		{
			return number > FormatVersion
				? Failure.Invalid($"Сборка создана более новой панелью (формат {number}); обновите панель")
				: Failure.Invalid($"Неизвестная версия формата сборки: {number}");
		}

		ModPackManifestPayload? manifest;
		try
		{
			manifest = document.Deserialize<ModPackManifestPayload>(PanelJson.Options);
		}
		catch (JsonException)
		{
			manifest = null;
		}

		// JSON null записывается поверх пустого списка по умолчанию: "mods": null — такая же
		// порча, как отсутствующий формат.
		if (manifest is null || manifest.Mods is null || manifest.Configs is null)
		{
			return Failure.Invalid("Опись сборки повреждена");
		}

		if (manifest.Mods.Count > MaximumMods)
		{
			return Failure.Invalid($"В сборке больше {MaximumMods} модов");
		}

		var mods = new List<ModPackModPayload>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var mod in manifest.Mods)
		{
			var id = ModId.Create(mod?.ModId);
			if (mod is null || !id.IsSuccess)
			{
				return Failure.Invalid($"Недопустимый modid в сборке: {mod?.ModId}");
			}

			var modVersion = (mod.Version ?? string.Empty).Trim();
			if (modVersion.Length > maximumVersionLength || modVersion.Any(char.IsControl))
			{
				return Failure.Invalid($"Недопустимая версия мода {id.Value.Value} в сборке");
			}

			if (!seen.Add(id.Value.Value))
			{
				return Failure.Invalid($"Мод {id.Value.Value} указан в сборке дважды");
			}

			mods.Add(mod with { ModId = id.Value.Value, Version = modVersion });
		}

		var configs = new List<string>();
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var raw in manifest.Configs)
		{
			var name = ModConfigName.Create(raw);
			if (!name.IsSuccess)
			{
				return Failure.Invalid($"Недопустимое имя файла настроек в описи сборки: {raw}");
			}

			if (!names.Add(name.Value.Value))
			{
				return Failure.Invalid($"Файл настроек {name.Value.Value} указан в сборке дважды");
			}

			configs.Add(name.Value.Value);
		}

		// Список пропущенных только поясняет, поэтому его порча сборку не отвергает.
		var skipped = (manifest.SkippedConfigs ?? [])
			.Where(item => item is not null)
			.Take(MaximumEntries)
			.Select(item => item with { Name = item.Name ?? string.Empty, Reason = Displayable(item.Reason) })
			.ToList();

		return manifest with { Mods = mods, Configs = configs, SkippedConfigs = skipped };
	}

	/// <summary>
	/// Причина пропуска из чужой сборки — фраза с чужим шаблоном и значениями. Своя выгрузка кладёт
	/// туда фразу панели, и получатель видит её на своём языке. Шаблон, который не собирается на
	/// одном из языков (<c>{3}</c> при одном значении, одинокая <c>{</c>), уронил бы ответ
	/// <c>string.Format</c>, поэтому такой показывается как есть, без подстановки.
	/// </summary>
	private static Phrase Displayable(Phrase? reason)
	{
		if (reason is null)
		{
			return Phrase.From(string.Empty);
		}

		try
		{
			_ = reason.ToString();
			_ = PhraseCatalog.Translate(reason, PanelLanguage.English);
			return reason;
		}
		catch (FormatException)
		{
			return Phrase.From(reason.Template);
		}
	}

	/// <summary>
	/// Что сборка изменит на сервере. <paramref name="current"/> — настройки сервера с теми же
	/// именами, что в сборке: нет ключа — файла нет, <c>null</c> — файл есть, но не прочитан.
	/// </summary>
	public static ModPackPlanPayload Plan(
		ModPackManifestPayload manifest,
		IReadOnlyDictionary<ModConfigName, JsonNode> pack,
		ModsPayload installed,
		IReadOnlyDictionary<ModConfigName, JsonNode?> current,
		GameVersionText game)
	{
		ArgumentNullException.ThrowIfNull(manifest);
		ArgumentNullException.ThrowIfNull(pack);
		ArgumentNullException.ThrowIfNull(installed);
		ArgumentNullException.ThrowIfNull(current);

		var byId = installed.Mods
			.Where(mod => mod.ModId is { Length: > 0 })
			.ToLookup(mod => mod.ModId!, StringComparer.OrdinalIgnoreCase);
		var wanted = manifest.Mods.Select(mod => mod.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);

		var mods = manifest.Mods.Select(mod =>
		{
			var archives = byId[mod.ModId].ToList();
			if (archives.Count == 0)
			{
				return new ModPackModPlanPayload { ModId = mod.ModId, DisplayName = mod.DisplayName, Version = mod.Version, Enabled = mod.Enabled, Status = "missing" };
			}

			if (archives.Count > 1)
			{
				return new ModPackModPlanPayload
				{
					ModId = mod.ModId,
					DisplayName = mod.DisplayName,
					Version = mod.Version,
					Enabled = mod.Enabled,
					Status = "ambiguous",
					Installed = string.Join(", ", archives.Select(archive => archive.Version ?? "—")),
				};
			}

			var archive = archives[0];
			return new ModPackModPlanPayload
			{
				ModId = mod.ModId,
				DisplayName = mod.DisplayName ?? archive.DisplayName,
				Version = mod.Version,
				Enabled = mod.Enabled,
				Status = SameVersion(mod.Version, archive.Version) ? "same" : "different",
				Installed = archive.Version,
				InstalledEnabled = archive.Enabled,
				Archive = archive.Name,
				Toggle = archive.Enabled != mod.Enabled,
			};
		}).ToList();

		var extra = installed.Mods
			.Where(mod => mod.ModId is not { Length: > 0 } id || !wanted.Contains(id))
			.OrderBy(mod => mod.ModId ?? mod.Name, StringComparer.OrdinalIgnoreCase)
			.Select(mod => new ModPackExtraPayload { Name = mod.Name, ModId = mod.ModId, DisplayName = mod.DisplayName, Enabled = mod.Enabled })
			.ToList();

		var configs = pack
			.OrderBy(config => config.Key.Value, StringComparer.OrdinalIgnoreCase)
			.Select(config => new ModPackConfigPlanPayload
			{
				Name = config.Key.Value,
				Status = !current.TryGetValue(config.Key, out var existing) ? "new"
					: existing is not null && JsonNode.DeepEquals(existing, config.Value) ? "same"
					: "changed",
			})
			.ToList();

		return new ModPackPlanPayload
		{
			Server = manifest.Server,
			ExportedAt = manifest.ExportedAt,
			GameVersion = manifest.GameVersion,
			CurrentGameVersion = game.Value,
			Mods = mods,
			Extra = extra,
			Configs = configs,
			SkippedConfigs = manifest.SkippedConfigs,
		};
	}

	/// <summary>
	/// Архивы, которые нужно переключить: включение как в сборке и, по выбору, отключение модов,
	/// которых в сборке нет. Моды не удаляются никогда.
	/// </summary>
	public static IReadOnlyList<ModArchiveName> Toggles(ModPackPlanPayload plan, bool matchSwitches, bool disableExtra)
	{
		ArgumentNullException.ThrowIfNull(plan);

		var switches = matchSwitches
			? plan.Mods.Where(mod => mod.Toggle && mod.Archive is not null).Select(mod => mod.Archive!)
			: [];
		var extra = disableExtra
			? plan.Extra.Where(mod => mod.Enabled).Select(mod => mod.Name)
			: [];

		return [.. switches.Concat(extra)
			.Select(ModArchiveName.Create)
			.Where(name => name.IsSuccess)
			.Select(name => name.Value)
			.Distinct()];
	}

	/// <summary>Версии совпадают без учёта регистра и приставки <c>v</c>.</summary>
	private static bool SameVersion(string? left, string? right) =>
		string.Equals(Plain(left), Plain(right), StringComparison.OrdinalIgnoreCase);

	private static string Plain(string? version)
	{
		var text = (version ?? string.Empty).Trim();
		return text.StartsWith('v') || text.StartsWith('V') ? text[1..] : text;
	}
}

/// <summary>Версия игры сервера для показа в плане сборки; пустая, если неизвестна.</summary>
public readonly record struct GameVersionText
{
	private GameVersionText(string? value) => Value = value;

	public string? Value { get; }

	public static GameVersionText From(string? text) =>
		new(string.IsNullOrWhiteSpace(text) ? null : text.Trim());
}
