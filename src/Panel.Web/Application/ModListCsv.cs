using System.Text;
using Panel.Contracts;
using Panel.Contracts.Content;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Перечень модов сервера в CSV: тот же, что раздел «Моды» показывает по <c>/api/mods</c>, для
/// таблицы или переписки.
/// </summary>
/// <remarks>
/// <para>
/// RFC 4180: запятая, CRLF, каждое поле в кавычках. UTF-8 с BOM — без него Excel читает файл в
/// кодировке ANSI и портит кириллицу. Первая строка — заголовки: служебные строки над таблицей
/// ломают разбор, сервер и время выгрузки есть в имени файла.
/// </para>
/// <para>
/// Путей на диске сервера нет: файл уходит из панели, а раскладка сервера получателю не нужна.
/// Название и остальные поля берутся из <c>modinfo.json</c> загруженного архива, то есть от
/// автора мода: поле, которое табличный редактор принял бы за формулу, экранируется апострофом.
/// </para>
/// </remarks>
public static class ModListCsv
{
	private const string ModDbSite = "https://mods.vintagestory.at/";

	public static byte[] Build(ModsPayload payload, PanelLanguage language)
	{
		ArgumentNullException.ThrowIfNull(payload);

		var text = PhraseCatalog.Renderer(language);
		var duplicates = payload.DuplicateIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var csv = new StringBuilder();
		Row(csv, Headers().Select(text));

		// Порядок по modid, как в списке ModDB; архив без modinfo.json — по имени файла.
		foreach (var mod in payload.Mods
			.OrderBy(mod => mod.ModId ?? mod.Name, StringComparer.OrdinalIgnoreCase)
			.ThenBy(mod => mod.Name, StringComparer.Ordinal))
		{
			Row(csv,
			[
				mod.DisplayName ?? mod.ModId ?? mod.Name,
				mod.ModId ?? string.Empty,
				mod.Version ?? string.Empty,
				mod.Side ?? string.Empty,
				text(Status(mod, duplicates)),
				string.Join("; ", mod.Dependencies
					.OrderBy(dependency => dependency.Key, StringComparer.OrdinalIgnoreCase)
					.Select(dependency => dependency.Value.Length > 0 ? $"{dependency.Key} ≥ {dependency.Value}" : dependency.Key)),
				string.Join("; ", Problems(mod, duplicates).Select(text)),
				mod.Name,
				mod.ModId is { Length: > 0 } id ? ModDbSite + Uri.EscapeDataString(id) : string.Empty,
			]);
		}

		return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
	}

	private static IEnumerable<Phrase> Headers() =>
	[
		Phrase.From("Название"),
		Phrase.From("Идентификатор"),
		Phrase.From("Версия"),
		Phrase.From("Сторона"),
		Phrase.From("Состояние"),
		Phrase.From("Зависимости"),
		Phrase.From("Проблемы"),
		Phrase.From("Файл"),
		Phrase.From("Страница мода"),
	];

	/// <summary>То же, что метка строки в разделе «Моды», и ошибка архива сверх неё.</summary>
	private static Phrase Status(ModEntryPayload mod, HashSet<string> duplicates)
	{
		if (mod.Error is not null)
		{
			return Phrase.From("Архив не прочитан");
		}

		if (!mod.Enabled)
		{
			return Phrase.From("Отключён");
		}

		return Problems(mod, duplicates).Any() ? Phrase.From("Есть проблемы") : Phrase.From("Включён");
	}

	private static IEnumerable<Phrase> Problems(ModEntryPayload mod, HashSet<string> duplicates)
	{
		if (mod.Error is { } error)
		{
			yield return error;
		}

		if (mod.ModId is { } id && duplicates.Contains(id))
		{
			yield return Phrase.Format($"modid {id} есть и у другого архива");
		}

		foreach (var issue in mod.DependencyIssues ?? [])
		{
			yield return issue.Status switch
			{
				"missing" when issue.Required.Length > 0 => Phrase.Format($"нет {issue.ModId} ≥ {issue.Required}"),
				"missing" => Phrase.Format($"нет {issue.ModId}"),
				"outdated" => Phrase.Format($"{issue.ModId}: установлена {issue.Installed ?? "—"}, нужна ≥ {issue.Required}"),
				_ => Phrase.Format($"{issue.ModId}: версию проверить не удалось (установлена {issue.Installed ?? "—"}, нужна {(issue.Required.Length > 0 ? issue.Required : "—")})"),
			};
		}
	}

	private static void Row(StringBuilder csv, IEnumerable<string> cells) =>
		csv.AppendJoin(',', cells.Select(Cell)).Append("\r\n");

	private static string Cell(string value)
	{
		// =, +, -, @ в начале — формула для Excel и LibreOffice, табуляция и возврат каретки — тоже,
		// после того как редактор их отбросит.
		if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
		{
			value = "'" + value;
		}

		return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
	}
}
