using System.Text.Json;

namespace Panel.Configuration.Localization;

/// <summary>
/// Подписи параметров мира из <c>assets/*/lang/*.json</c>: только ключи с приставками
/// <c>worldattribute-</c>, <c>worldconfig-</c>, <c>playstyle-</c> — панели не нужен весь файл.
/// </summary>
/// <remarks>
/// Не доменный тип: ключ здесь не идентификатор, который нуждается в проверке, а готовая
/// строка вида <c>"worldattribute-" + код</c>, которую составляет вызывающий. Поэтому класс лежит
/// вне <c>.Domain</c> — переводчик текста, а не часть модели предметной области.
/// </remarks>
public sealed class WorldLanguagePack
{
	private readonly IReadOnlyDictionary<string, string> primary;
	private readonly IReadOnlyDictionary<string, string> fallback;

	private WorldLanguagePack(IReadOnlyDictionary<string, string> primary, IReadOnlyDictionary<string, string> fallback)
	{
		this.primary = primary;
		this.fallback = fallback;
	}

	public static readonly WorldLanguagePack Empty = new(
		new Dictionary<string, string>(), new Dictionary<string, string>());

	/// <param name="primaryJson">Например <c>ru.json</c>; ключ отсутствует — берётся запасной.</param>
	/// <param name="fallbackJson">Например <c>en.json</c>.</param>
	public static WorldLanguagePack From(string? primaryJson, string? fallbackJson) =>
		new(Filtered(primaryJson), Filtered(fallbackJson));

	/// <summary>
	/// Несколько пакетов в один: игра, затем моды, в переданном порядке. Первый источник, у
	/// которого есть ключ, выигрывает — тот же порядок приоритета, что у слияния схемы.
	/// </summary>
	public static WorldLanguagePack Merge(IEnumerable<WorldLanguagePack> packs)
	{
		var primary = new Dictionary<string, string>(StringComparer.Ordinal);
		var fallback = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var pack in packs)
		{
			foreach (var (key, value) in pack.primary)
			{
				primary.TryAdd(key, value);
			}

			foreach (var (key, value) in pack.fallback)
			{
				fallback.TryAdd(key, value);
			}
		}

		return new WorldLanguagePack(primary, fallback);
	}

	/// <summary>Порядок поиска: основной язык → запасной → <paramref name="fallbackText"/> → сам код.</summary>
	public string Label(string key, string fallbackText) =>
		primary.GetValueOrDefault(key) ?? fallback.GetValueOrDefault(key)
			?? (fallbackText.Length > 0 ? fallbackText : key);

	public string? Find(string key) => primary.GetValueOrDefault(key) ?? fallback.GetValueOrDefault(key);

	private static IReadOnlyDictionary<string, string> Filtered(string? json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return new Dictionary<string, string>();
		}

		try
		{
			// JsonDocument, не JsonNode: языковые файлы игры содержат повторяющиеся ключи (опечатки
			// в моде или в самой игре), а JsonObject на такой JSON бросает исключение при переборе.
			using var document = JsonDocument.Parse(json);
			if (document.RootElement.ValueKind != JsonValueKind.Object)
			{
				return new Dictionary<string, string>();
			}

			var result = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var property in document.RootElement.EnumerateObject())
			{
				if (Wanted(property.Name) && property.Value.ValueKind == JsonValueKind.String)
				{
					result[property.Name] = property.Value.GetString()!;
				}
			}

			return result;
		}
		catch (JsonException)
		{
			return new Dictionary<string, string>();
		}
	}

	private static bool Wanted(string key) =>
		key.StartsWith("worldattribute-", StringComparison.Ordinal)
		|| key.StartsWith("worldconfig-", StringComparison.Ordinal)
		|| key.StartsWith("playstyle-", StringComparison.Ordinal);
}
