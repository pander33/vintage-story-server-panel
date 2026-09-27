using System.Globalization;
using System.Text.Json.Nodes;

namespace Panel.Catalogues.Domain;

/// <summary>Тег ModDB для фильтра: название и цвет.</summary>
public sealed record CatalogueTag(string Name, string Color);

/// <summary>Порядок каталога. «Новые» нет: даты создания мода в списке ModDB нет, есть только <c>lastreleased</c>.</summary>
public enum CatalogueSort
{
	Downloads,
	Follows,
	Trending,
	Updated,
}

/// <summary>Карточка мода из общего списка ModDB.</summary>
/// <param name="Identifiers">Строковые идентификаторы (<c>modidstrs</c>); по ним мод открывается на ModDB.</param>
/// <param name="LastReleased">Как отдаёт ModDB: <c>2026-09-27 00:32:28</c>, порядок совпадает с лексикографическим.</param>
public sealed record CatalogueEntry(
	long ModId,
	IReadOnlyList<string> Identifiers,
	string Name,
	string Summary,
	string Author,
	string Side,
	IReadOnlyList<string> Tags,
	long Downloads,
	long Follows,
	long Trending,
	string LastReleased,
	string? Logo,
	string? UrlAlias)
{
	/// <summary>По чему открывать страницу мода: псевдоним, затем первый строковый идентификатор, затем номер.</summary>
	public string LookupKey =>
		!string.IsNullOrEmpty(UrlAlias) ? UrlAlias
		: Identifiers.Count > 0 ? Identifiers[0]
		: ModId.ToString(CultureInfo.InvariantCulture);

	/// <summary>
	/// Установлен ли мод: <c>modid</c> из <c>modinfo.json</c> совпадает с одним из строковых
	/// идентификаторов ModDB без учёта регистра. Набор установленных — в нижнем регистре.
	/// </summary>
	public bool IsAmong(IReadOnlySet<string> installed)
	{
		ArgumentNullException.ThrowIfNull(installed);
		return Identifiers.Any(id => installed.Contains(id.ToLowerInvariant()));
	}

	/// <summary>Запись списка или <c>null</c>, если это не мод (другой тип записи) или нет номера.</summary>
	public static CatalogueEntry? From(JsonObject json)
	{
		ArgumentNullException.ThrowIfNull(json);
		if (Text(json["type"]) is { Length: > 0 } type && type != "mod")
		{
			return null;
		}

		if (Number(json["modid"]) is not { } id)
		{
			return null;
		}

		return new CatalogueEntry(
			id,
			Strings(json["modidstrs"]),
			Text(json["name"]),
			Text(json["summary"]),
			Text(json["author"]),
			Text(json["side"]),
			Strings(json["tags"]),
			Number(json["downloads"]) ?? 0,
			Number(json["follows"]) ?? 0,
			Number(json["trendingpoints"]) ?? 0,
			Text(json["lastreleased"]),
			Text(json["logo"]) is { Length: > 0 } logo ? logo : null,
			Text(json["urlalias"]) is { Length: > 0 } alias ? alias : null);
	}

	private static string Text(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

	private static long? Number(JsonNode? node)
	{
		if (node is not JsonValue value)
		{
			return null;
		}

		if (value.TryGetValue<long>(out var number))
		{
			return number;
		}

		if (value.TryGetValue<int>(out var small))
		{
			return small;
		}

		return value.TryGetValue<double>(out var real) && real == Math.Floor(real) && Math.Abs(real) < long.MaxValue ? (long) real : null;
	}

	private static IReadOnlyList<string> Strings(JsonNode? node) =>
		node is JsonArray array
			? [.. array.Select(Text).Where(item => item.Length > 0)]
			: [];
}

/// <summary>Что просит интерфейс: порядок, отбор и страница.</summary>
public sealed record CatalogueQuery
{
	public const int MaxLimit = 60;

	public CatalogueSort Sort { get; init; } = CatalogueSort.Downloads;

	/// <summary>Названия тегов; запись подходит, если у неё есть все.</summary>
	public IReadOnlyList<string> Tags { get; init; } = [];

	/// <summary>Пусто — любая сторона.</summary>
	public string Side { get; init; } = string.Empty;

	public bool CompatibleOnly { get; init; }

	/// <summary>Убрать моды, уже установленные на сервере; без списка установленных не действует.</summary>
	public bool HideInstalled { get; init; }

	/// <summary>Подстрока имени или автора без учёта регистра; пусто — без отбора.</summary>
	public string Text { get; init; } = string.Empty;

	public int Offset { get; init; }

	public int Limit { get; init; } = 30;

	/// <summary>Запрос из параметров адреса. Неизвестный порядок — по скачиваниям, страница приводится к пределам.</summary>
	public static CatalogueQuery ParseParameters(
		string? sort, IEnumerable<string?> tags, string? side, bool compatibleOnly, string? text, int? offset, int? limit) =>
		new()
		{
			Sort = sort?.Trim().ToLowerInvariant() switch
			{
				"follows" => CatalogueSort.Follows,
				"trending" => CatalogueSort.Trending,
				"updated" => CatalogueSort.Updated,
				_ => CatalogueSort.Downloads,
			},
			Tags = [.. tags.Select(tag => tag?.Trim() ?? string.Empty).Where(tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(8)],
			Side = side?.Trim().ToLowerInvariant() is "both" or "server" or "client" ? side.Trim().ToLowerInvariant() : string.Empty,
			CompatibleOnly = compatibleOnly,
			Text = Limited(text),
			Offset = Math.Max(0, offset ?? 0),
			Limit = Math.Clamp(limit ?? 30, 1, MaxLimit),
		};

	private static string Limited(string? text)
	{
		var trimmed = (text ?? string.Empty).Trim();
		return trimmed.Length <= 80 ? trimmed : trimmed[..80];
	}
}

/// <summary>Страница отобранных записей и сколько их всего.</summary>
public sealed record CataloguePage(IReadOnlyList<CatalogueEntry> Items, int Total);

/// <summary>Отбор, порядок и страница каталога. Чистая функция над списком в памяти.</summary>
public static class ModCatalogue
{
	/// <param name="compatible">Номера модов, отмеченных совместимыми с версией игры; <c>null</c>, если это неизвестно.</param>
	/// <param name="installed"><c>modid</c> установленных модов в нижнем регистре; <c>null</c>, если это неизвестно.</param>
	public static CataloguePage Page(
		IReadOnlyList<CatalogueEntry> entries,
		CatalogueQuery query,
		IReadOnlySet<long>? compatible,
		IReadOnlySet<string>? installed = null)
	{
		ArgumentNullException.ThrowIfNull(entries);
		ArgumentNullException.ThrowIfNull(query);

		var chosen = entries.Where(entry => Matches(entry, query, compatible)
			&& !(query.HideInstalled && installed is not null && entry.IsAmong(installed)));
		var ordered = query.Sort switch
		{
			CatalogueSort.Follows => chosen.OrderByDescending(entry => entry.Follows).ThenByDescending(entry => entry.Downloads),
			CatalogueSort.Trending => chosen.OrderByDescending(entry => entry.Trending).ThenByDescending(entry => entry.Downloads),
			CatalogueSort.Updated => chosen.OrderByDescending(entry => entry.LastReleased, StringComparer.Ordinal).ThenByDescending(entry => entry.Downloads),
			_ => chosen.OrderByDescending(entry => entry.Downloads).ThenByDescending(entry => entry.Follows),
		};

		var all = ordered.ThenBy(entry => entry.ModId).ToList();
		return new CataloguePage([.. all.Skip(query.Offset).Take(query.Limit)], all.Count);
	}

	/// <summary>
	/// Теги для фильтра по частоте в каталоге: у ModDB их две сотни, большей частью почти пустых.
	/// Тег, которого нет ни у одного мода, не показывается; у равных по частоте — по имени.
	/// </summary>
	public static IReadOnlyList<(CatalogueTag Tag, int Count)> PopularTags(IReadOnlyList<CatalogueEntry> entries, IReadOnlyList<CatalogueTag> tags)
	{
		ArgumentNullException.ThrowIfNull(entries);
		ArgumentNullException.ThrowIfNull(tags);
		var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (var tag in entries.SelectMany(entry => entry.Tags))
		{
			counts[tag] = counts.GetValueOrDefault(tag) + 1;
		}

		return
		[
			.. tags
				.Select(tag => (Tag: tag, Count: counts.GetValueOrDefault(tag.Name)))
				.Where(item => item.Count > 0)
				.OrderByDescending(item => item.Count)
				.ThenBy(item => item.Tag.Name, StringComparer.OrdinalIgnoreCase),
		];
	}

	private static bool Matches(CatalogueEntry entry, CatalogueQuery query, IReadOnlySet<long>? compatible)
	{
		if (query.Side.Length > 0 && !string.Equals(entry.Side, query.Side, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		if (query.CompatibleOnly && compatible is not null && !compatible.Contains(entry.ModId))
		{
			return false;
		}

		if (query.Tags.Any(tag => !entry.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
		{
			return false;
		}

		return query.Text.Length == 0
			|| entry.Name.Contains(query.Text, StringComparison.OrdinalIgnoreCase)
			|| entry.Author.Contains(query.Text, StringComparison.OrdinalIgnoreCase)
			|| entry.Identifiers.Any(id => id.Contains(query.Text, StringComparison.OrdinalIgnoreCase));
	}
}
