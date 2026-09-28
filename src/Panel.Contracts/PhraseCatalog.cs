using System.Collections.Frozen;
using System.Text.Json;
using Panel.SharedKernel;

namespace Panel.Contracts;

/// <summary>Язык интерфейса, выбранный пользователем.</summary>
public enum PanelLanguage
{
	English,
	Russian,
}

/// <summary>
/// Переводит <see cref="Phrase"/> на выбранный язык. Шаблоны русские; английский каталог —
/// ресурс сборки <c>Resources/phrases-en.json</c>, ключ — шаблон целиком.
/// </summary>
/// <remarks>
/// <para>
/// Каталог живёт в опубликованном языке, а не в панели: фразы показывает и установщик
/// (<c>vs-panel</c>), который на панель не ссылается. Язык HTTP-запроса выбирает панель
/// (<c>LocalizedJson</c>), терминала — английский, как весь вывод установщика.
/// </para>
/// <para>
/// Шаблона нет в каталоге — текст остаётся русским целиком, без перевода отдельных значений:
/// одна фраза не смешивает языки. Строка-значение переводится, только если она сама есть в
/// каталоге (готовый текст отказа внутри другого).
/// </para>
/// </remarks>
public static class PhraseCatalog
{
	private static readonly FrozenDictionary<string, string> english = Load("phrases-en.json");

	/// <summary>Шаблоны английского каталога: для проверки полноты в тестах.</summary>
	public static IReadOnlyDictionary<string, string> English => english;

	/// <summary>Код языка (<c>ru</c>, <c>en</c>) как в cookie; всё, кроме <c>ru</c>, — английский.</summary>
	public static PanelLanguage Parse(string? code) => code == "ru" ? PanelLanguage.Russian : PanelLanguage.English;

	public static string Code(PanelLanguage language) => language == PanelLanguage.Russian ? "ru" : "en";

	/// <summary>Показ фразы на языке — для текста, который уходит не в HTTP-ответ.</summary>
	public static Func<Phrase, string> Renderer(PanelLanguage language) => phrase => Translate(phrase, language);

	public static string Translate(Phrase phrase, PanelLanguage language)
	{
		if (language == PanelLanguage.Russian)
		{
			return phrase.ToString();
		}

		// Шаблон без русских букв ("Watchdog: {0}", имя игрока) переводить не нужно, но значения в
		// нём — нужно.
		if (!english.TryGetValue(phrase.Template, out var template))
		{
			if (phrase.Template.Any(IsCyrillic))
			{
				return phrase.ToString();
			}

			template = phrase.Template;
		}

		return Phrase.Render(template, phrase.Args.Select(arg => arg is Phrase nested ? Translate(nested, language) : TranslateText(arg.ToString() ?? string.Empty)));
	}

	private static bool IsCyrillic(char symbol) => symbol is >= 'Ѐ' and <= 'ӿ';

	private static string TranslateText(string text) =>
		english.TryGetValue(Phrase.Escape(text), out var template) ? Phrase.Render(template, []) : text;

	private static FrozenDictionary<string, string> Load(string name)
	{
		using var stream = typeof(PhraseCatalog).Assembly.GetManifestResourceStream("Panel.Contracts.Resources." + name)
			?? throw new InvalidOperationException($"Нет встроенного каталога {name}");
		return (JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? []).ToFrozenDictionary(StringComparer.Ordinal);
	}
}
