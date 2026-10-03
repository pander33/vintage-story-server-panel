using System.Text.Json.Nodes;

namespace Panel.Runtime.Domain;

/// <summary>Одна запись в списке забаненных или добавленных в белый список игроков.</summary>
/// <remarks>
/// Поле <see cref="UntilSeconds"/> отсутствует у белого списка и у бана «навсегда»; такой бан
/// отличается от бана на срок только отсутствием значения, а не отдельным флагом (детализация
/// интерфейса 2.1 §3).
/// </remarks>
public sealed record PlayerListEntry(
	string PlayerName,
	string? PlayerUid,
	string? IssuedBy,
	long? UntilSeconds,
	string? Reason)
{
	/// <summary>
	/// Бан навсегда: срока нет, либо игра записала дату далеко в будущем вместо признака
	/// «навсегда» (переход через это значение — часть открытого вопроса плана, требует сверки
	/// с фикстурой настоящего сервера).
	/// </summary>
	public bool Forever => UntilSeconds is null || UntilSeconds.Value > distantFuture;

	/// <summary>Условная граница «далёкого будущего» — год 3000 в секундах Unix.</summary>
	private const long distantFuture = 32503680000;
}

/// <summary>
/// Оба списка одного сервера, разобранные из <c>playersbanned.json</c> и
/// <c>playerswhitelisted.json</c> (детализация интерфейса 2.1 §3).
/// </summary>
public sealed record PlayerLists(IReadOnlyList<PlayerListEntry> Banned, IReadOnlyList<PlayerListEntry> Whitelisted)
{
	public static readonly PlayerLists Empty = new([], []);

	/// <summary>
	/// Разбор одного файла списка. Точный набор полей игра не документирует и на тестовом
	/// сервере оба файла были пусты (открытый вопрос плана): разбор допускает несколько
	/// вероятных написаний одного и того же поля, неизвестные поля пропускает, а отсутствующие
	/// отдаёт как <c>null</c> вместо отказа. Пустой или отсутствующий файл — пустой список, а не
	/// ошибка; ошибка — только у нечитаемого JSON.
	/// </summary>
	public static IReadOnlyList<PlayerListEntry> ParseEntries(string json)
	{
		ArgumentNullException.ThrowIfNull(json);

		if (string.IsNullOrWhiteSpace(json))
		{
			return [];
		}

		var node = JsonNode.Parse(json);
		var array = node switch
		{
			JsonArray direct => direct,
			JsonObject wrapper when wrapper.FirstOrDefault(pair => pair.Value is JsonArray).Value is JsonArray inner => inner,
			_ => null,
		};

		if (array is null)
		{
			return [];
		}

		var entries = new List<PlayerListEntry>();
		foreach (var item in array)
		{
			if (item is not JsonObject record)
			{
				continue;
			}

			var name = Text(record, "PlayerName", "Playername", "Name", "player_name");
			if (string.IsNullOrWhiteSpace(name))
			{
				continue;
			}

			entries.Add(new PlayerListEntry(
				name,
				Text(record, "PlayerUID", "PlayerUid", "Uid", "UID"),
				Text(record, "IssuedByPlayerName", "IssuedBy", "issued_by"),
				Seconds(record, "UntilDate", "Until", "until_date"),
				Text(record, "Reason", "Motivation", "reason")));
		}

		return entries;
	}

	private static string? Text(JsonObject record, params string[] keys)
	{
		foreach (var key in keys)
		{
			if (record[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
			{
				return text;
			}
		}

		return null;
	}

	/// <summary>
	/// Дата как число секунд Unix или как строка ISO — формат в игре не проверен (открытый
	/// вопрос плана), поэтому распознаются оба варианта.
	/// </summary>
	private static long? Seconds(JsonObject record, params string[] keys)
	{
		foreach (var key in keys)
		{
			if (record[key] is not JsonValue value)
			{
				continue;
			}

			if (value.TryGetValue<long>(out var seconds))
			{
				return seconds;
			}

			if (value.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, out var parsed))
			{
				return parsed.ToUnixTimeSeconds();
			}
		}

		return null;
	}
}
