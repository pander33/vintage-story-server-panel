using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.SharedKernel;

namespace Panel.Configuration.Domain;

/// <summary>Тип значения параметра мира, как объявляет игра.</summary>
public enum WorldAttributeType
{
	Bool,
	Dropdown,

	/// <summary>Целое число: <c>IntInput</c> или <c>IntRange</c> в API игры.</summary>
	Integer,

	/// <summary>Дробное число: <c>DoubleInput</c> в API игры.</summary>
	Number,

	/// <summary>Свободная строка: <c>String</c> в API игры.</summary>
	Text,

	/// <summary>Тип, которого нет в этом перечне: параметр показывается текстовым полем, а не отбрасывается.</summary>
	Unknown,
}

/// <summary>Один параметр мира из <c>worldConfigAttributes</c> игры или мода.</summary>
/// <param name="Code">Ключ в <c>WorldConfiguration</c>.</param>
/// <param name="Category">Раздел формы; из данных игры или <see cref="UncategorizedCategory"/>.</param>
/// <param name="Values">Допустимые значения выбора; для <see cref="WorldAttributeType.Dropdown"/>.</param>
/// <param name="Names">Английские подписи значений, по индексу как <paramref name="Values"/>; запасной вариант подписи.</param>
/// <param name="OnlyDuringWorldCreate">Параметр можно задать только при создании мира, не в работающем.</param>
/// <param name="Source">Кто объявил параметр: <c>"game"</c> или идентификатор мода.</param>
public sealed record WorldAttribute(
	string Code,
	WorldAttributeType Type,
	string Category,
	IReadOnlyList<string> Values,
	IReadOnlyList<string> Names,
	string? Default,
	bool OnlyDuringWorldCreate,
	string Source)
{
	public const string UncategorizedCategory = "other";
}

/// <summary>Стиль игры из <c>playstyles</c>: набор готовых значений параметров плюс который world type.</summary>
/// <param name="Code">Значение <c>WorldConfig.PlayStyle</c>.</param>
/// <param name="LanguageCode">Ключ подписи (<c>playstyle-&lt;langCode&gt;</c>); у игры это не всегда <c>preset-&lt;code&gt;</c>.</param>
/// <param name="PlayListCode">Группа: <c>survival</c> или <c>creative</c>.</param>
/// <param name="WorldType">Тип генерации мира: <c>standard</c> или <c>superflat</c>.</param>
/// <param name="Values">Готовые значения параметров этого стиля; ключи вне схемы (у творческого стиля) сохраняются как есть.</param>
public sealed record WorldPlaystyle(
	string Code,
	string LanguageCode,
	string PlayListCode,
	string WorldType,
	int Order,
	IReadOnlyList<string> Mods,
	IReadOnlyDictionary<string, string> Values,
	string Source);

/// <summary>Разобранная схема параметров мира: стили и параметры, собранные из игры и модов.</summary>
/// <param name="Warnings">Например конфликт кода параметра или стиля между двумя источниками.</param>
public sealed record WorldSchema(
	IReadOnlyList<WorldPlaystyle> Playstyles,
	IReadOnlyList<WorldAttribute> Attributes,
	IReadOnlyList<Phrase> Warnings)
{
	public static readonly WorldSchema Empty = new([], [], []);

	/// <summary>
	/// Разбор одного источника — строки <c>WorldConfig</c> из атрибута <c>ModInfo</c> сборки игры или
	/// мода, или значения ключа <c>worldConfig</c> в <c>modinfo.json</c> мода без своей сборки.
	/// </summary>
	/// <remarks>
	/// Разбор не исполняет ничего из сборки: строка приходит уже прочитанной демоном через
	/// метаданные (<c>System.Reflection.Metadata</c>), сюда попадает только текст JSON.
	/// Неразборчивый источник не роняет всю схему: вызывающий добавляет предупреждение и
	/// продолжает со следующим источником.
	/// </remarks>
	public static Result<WorldSchema> ParseSource(string json, string source)
	{
		ArgumentException.ThrowIfNullOrEmpty(source);
		try
		{
			return ParseDocument(json, source);
		}
		catch (ArgumentException exception)
		{
			// Повторяющийся ключ: JsonObject бросает при первом обращении к содержимому.
			return Failure.Invalid($"{source}: не удалось разобрать JSON схемы мира: {exception.Message}");
		}
	}

	private static Result<WorldSchema> ParseDocument(string json, string source)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return WorldSchema.Empty;
		}

		JsonNode? node;
		try
		{
			node = JsonNode.Parse(json);
		}
		catch (JsonException exception)
		{
			return Failure.Invalid($"{source}: не удалось разобрать JSON схемы мира: {exception.Message}");
		}

		if (node is not JsonObject document)
		{
			return Failure.Invalid($"{source}: схема мира должна быть JSON-объектом");
		}

		var playstyles = new List<WorldPlaystyle>();
		foreach (var item in (document["playstyles"] as JsonArray ?? []).OfType<JsonObject>())
		{
			if (Text(item["code"]) is not { Length: > 0 } code)
			{
				continue;
			}

			var values = new Dictionary<string, string>(StringComparer.Ordinal);
			if (item["worldConfig"] is JsonObject config)
			{
				foreach (var (key, value) in config)
				{
					values[key] = Text(value);
				}
			}

			playstyles.Add(new WorldPlaystyle(
				code,
				Text(item["langCode"]) is { Length: > 0 } lang ? lang : "preset-" + code,
				Text(item["playListCode"]),
				Text(item["worldType"]) is { Length: > 0 } type ? type : "standard",
				(int) (Integer(item["listOrder"]) ?? 0),
				[.. (item["mods"] as JsonArray ?? []).Select(Text).Where(mod => mod.Length > 0)],
				values,
				source));
		}

		var attributes = new List<WorldAttribute>();
		foreach (var item in (document["worldConfigAttributes"] as JsonArray ?? []).OfType<JsonObject>())
		{
			if (Text(item["code"]) is not { Length: > 0 } code)
			{
				continue;
			}

			attributes.Add(new WorldAttribute(
				code,
				ParseType(Text(item["dataType"])),
				Text(item["category"]) is { Length: > 0 } category ? category : WorldAttribute.UncategorizedCategory,
				[.. (item["values"] as JsonArray ?? []).Select(Text)],
				[.. (item["names"] as JsonArray ?? []).Select(Text)],
				item["default"] is null ? null : Text(item["default"]),
				Truthy(item["onlyDuringWorldCreate"]),
				source));
		}

		return new WorldSchema(playstyles, attributes, []);
	}

	/// <summary>
	/// Схема игры, затем модов в переданном порядке. Одинаковый код у двух источников — берётся
	/// первый, конфликт уходит в <see cref="Warnings"/> вместо отказа: показ схемы не должен
	/// останавливаться из-за одного мода с плохими метаданными.
	/// </summary>
	public static WorldSchema Merge(IEnumerable<Result<WorldSchema>> sources)
	{
		var playstyles = new List<WorldPlaystyle>();
		var attributes = new List<WorldAttribute>();
		var warnings = new List<Phrase>();
		var playstyleCodes = new HashSet<string>(StringComparer.Ordinal);
		var attributeCodes = new HashSet<string>(StringComparer.Ordinal);

		foreach (var source in sources)
		{
			if (!source.IsSuccess)
			{
				warnings.Add(source.Failure!.Text);
				continue;
			}

			foreach (var style in source.Value.Playstyles)
			{
				if (playstyleCodes.Add(style.Code))
				{
					playstyles.Add(style);
				}
				else
				{
					warnings.Add(Phrase.Format($"Стиль «{style.Code}» уже объявлен другим источником ({style.Source}) — использован первый"));
				}
			}

			foreach (var attribute in source.Value.Attributes)
			{
				if (attributeCodes.Add(attribute.Code))
				{
					attributes.Add(attribute);
				}
				else
				{
					warnings.Add(Phrase.Format($"Параметр «{attribute.Code}» уже объявлен другим источником ({attribute.Source}) — использован первый"));
				}
			}
		}

		return new WorldSchema(
			[.. playstyles.OrderBy(style => style.Order)],
			attributes,
			warnings);
	}

	/// <summary>
	/// Значение параметра из шаблона мира (все значения там — строки), проверенное по типу; выбор
	/// проверяется списком <see cref="WorldAttribute.Values"/>. Отказ, а не булев ответ: сообщение
	/// сразу называет параметр, как и остальные проверки схемы настроек.
	/// </summary>
	public static Result<string> ParseValue(WorldAttribute attribute, JsonNode? value)
	{
		ArgumentNullException.ThrowIfNull(attribute);
		var text = Text(value);
		var valid = attribute.Type switch
		{
			WorldAttributeType.Bool => text is "true" or "false",
			WorldAttributeType.Dropdown => attribute.Values.Contains(text, StringComparer.Ordinal),
			WorldAttributeType.Integer => long.TryParse(text, out _),
			WorldAttributeType.Number => double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out _),
			_ => true,
		};

		return valid ? text : Failure.Invalid($"{attribute.Code}: недопустимое значение параметра мира");
	}

	/// <summary>
	/// Тип без учёта регистра: игра 1.22.7 пишет <c>bool</c> и <c>dropdown</c> строчными, а имена в
	/// API игры (<c>EnumDataType</c>) — с заглавной, и моды пишут как придётся.
	/// </summary>
	private static WorldAttributeType ParseType(string dataType) => dataType.ToLowerInvariant() switch
	{
		"bool" => WorldAttributeType.Bool,
		"dropdown" => WorldAttributeType.Dropdown,
		"intinput" or "intrange" => WorldAttributeType.Integer,
		"doubleinput" => WorldAttributeType.Number,
		"string" => WorldAttributeType.Text,
		_ => WorldAttributeType.Unknown,
	};

	private static string Text(JsonNode? node) => node switch
	{
		null => string.Empty,
		JsonValue value when value.TryGetValue<string>(out var text) => text,
		JsonValue value when value.TryGetValue<bool>(out var flag) => flag ? "true" : "false",
		JsonValue value => value.ToJsonString().Trim('"'),
		_ => string.Empty,
	};

	/// <summary>Истинность как у Python <c>bool(value)</c>: <c>true</c>, ненулевое число, непустая строка.</summary>
	private static bool Truthy(JsonNode? node) => node switch
	{
		null => false,
		JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
		JsonValue value when value.TryGetValue<string>(out var text) => text.Length > 0,
		JsonValue value when value.TryGetValue<double>(out var number) => number != 0,
		_ => true,
	};

	private static long? Integer(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<long>(out var number) ? number
		: node is JsonValue small && small.TryGetValue<int>(out var integer) ? integer
		: null;
}
