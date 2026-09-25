using Panel.SharedKernel;

namespace Panel.Content.Domain;

/// <summary>
/// Хранилища «на всякий случай»: заменённые моды, копии настроек сервера, копии настроек
/// модов.
/// </summary>
/// <remarks>
/// Политика удержания резервных копий их не касается, поэтому без очистки они растут
/// бесконечно и загромождают свои списки выбора.
/// </remarks>
public enum HistoryStore
{
	ModHistory,
	ConfigBackup,
	ModConfigBackup,
}

public static class HistoryStoreWire
{
	public static IReadOnlyList<HistoryStore> All { get; } =
		[HistoryStore.ModHistory, HistoryStore.ConfigBackup, HistoryStore.ModConfigBackup];

	public static string ToWire(HistoryStore store) => store switch
	{
		HistoryStore.ModHistory => "mod-history",
		HistoryStore.ConfigBackup => "config-backup",
		_ => "mod-config-backup",
	};

	public static Result<HistoryStore> Parse(string? raw) => raw switch
	{
		"mod-history" => HistoryStore.ModHistory,
		"config-backup" => HistoryStore.ConfigBackup,
		"mod-config-backup" => HistoryStore.ModConfigBackup,
		_ => Failure.Invalid("Неизвестное хранилище истории"),
	};
}

/// <summary>
/// Ключ записи истории: имя файла, для истории модов — <c>&lt;modid&gt;/&lt;файл&gt;</c>.
/// </summary>
/// <remarks>
/// Ключ не превращается в путь: демон заново строит индекс хранилища и ищет ключ в нём.
/// Здесь отсекаются только заведомо неверные ключи до обращения к демону.
/// </remarks>
public readonly record struct HistoryKey
{
	private HistoryKey(string value) => Value = value;

	public string Value { get; }

	public static Result<HistoryKey> Create(string? raw)
	{
		var value = (raw ?? string.Empty).Trim();
		var parts = value.Split('/');

		return value.Length is > 0 and <= 400 && parts.Length <= 2 && parts.All(PlainFileName.IsPlain)
			? new HistoryKey(value)
			: Failure.Invalid("Недопустимый ключ записи истории");
	}

	public override string ToString() => Value ?? string.Empty;
}

/// <summary>Запись истории: ключ, группа (мод или файл настроек), размер, время.</summary>
public sealed record HistoryEntry(string Key, string Group, string Name, long Size, DateTimeOffset Modified);

/// <summary>Сколько новейших записей оставить в каждой группе.</summary>
public readonly record struct HistoryKeep
{
	private HistoryKeep(int count) => Count = count;

	public int Count { get; }

	public static Result<HistoryKeep> Create(int count) =>
		count is >= 1 and <= 100
			? new HistoryKeep(count)
			: Failure.Invalid("Число оставляемых записей должно быть от 1 до 100");
}

/// <summary>Правила истории: порядок списка и выбор того, что удалит очистка.</summary>
public static class HistoryRules
{
	/// <summary>Порядок списка: по группе без учёта регистра, новые первыми.</summary>
	public static IReadOnlyList<HistoryEntry> Ordered(IEnumerable<HistoryEntry> entries) =>
	[
		.. entries
			.OrderBy(entry => entry.Group.ToLowerInvariant(), StringComparer.Ordinal)
			.ThenByDescending(entry => entry.Modified),
	];

	/// <summary>
	/// Что удалить, чтобы в каждой группе осталось <paramref name="keep"/> новейших. Группа —
	/// мод или файл настроек, чтобы «оставить пять» не оставило пять версий одного мода и ни
	/// одной другого.
	/// </summary>
	public static IReadOnlyList<HistoryEntry> Prune(IEnumerable<HistoryEntry> entries, HistoryKeep keep) =>
	[
		.. entries
			.GroupBy(entry => entry.Group, StringComparer.Ordinal)
			.SelectMany(group => group
				.OrderByDescending(entry => entry.Modified)
				.ThenByDescending(entry => entry.Key, StringComparer.Ordinal)
				.Skip(keep.Count)),
	];
}
