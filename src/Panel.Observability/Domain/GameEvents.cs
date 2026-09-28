using Panel.SharedKernel;

namespace Panel.Observability.Domain;

/// <summary>
/// Событие внутри игры: чат, вход, выход, смерть, возрождение. Их пишет мод телеметрии в
/// <c>VsPanel/events.jsonl</c>, отдельно от снимка.
/// </summary>
/// <remarks>
/// На шину уведомлений эти события не попадают: оживлённый сервер пишет в чат больше, чем
/// стоит отправлять в Discord или Telegram.
/// </remarks>
public sealed record GameEvent(
	long At,
	long Seq,
	string Kind,
	string Player,
	string Uid,
	long? Channel,
	string Text)
{
	/// <summary>Виды событий, которые пишет мод и показывает панель; остальные строки отбрасываются.</summary>
	public static IReadOnlyList<string> Kinds { get; } = ["chat", "join", "leave", "death", "respawn"];

	/// <summary>
	/// Смерть приходит как <c>EnumDamageSource/EnumDamageType</c>. Переводится только источник:
	/// он и важен владельцу сервера.
	/// </summary>
	private static readonly Dictionary<string, Phrase> deathSources = new(StringComparer.Ordinal)
	{
		["Block"] = Phrase.From("блок"),
		["Player"] = Phrase.From("другой игрок"),
		["Entity"] = Phrase.From("существо"),
		["Explosion"] = Phrase.From("взрыв"),
		["Fall"] = Phrase.From("падение"),
		["Drown"] = Phrase.From("утопление"),
		["Void"] = Phrase.From("пустота"),
		["Suicide"] = Phrase.From("самоубийство"),
		["Internal"] = Phrase.From("внутренняя причина"),
		["Weather"] = Phrase.From("погода"),
		["Machine"] = Phrase.From("механизм"),
		["Revive"] = Phrase.From("воскрешение"),
		["Unknown"] = Phrase.From("неизвестно"),
	};

	/// <summary>Причина смерти для интерфейса; у прочих событий пусто.</summary>
	public Phrase? Detail
	{
		get
		{
			if (Kind != "death")
			{
				return null;
			}

			var slash = Text.IndexOf('/', StringComparison.Ordinal);
			var source = slash < 0 ? Text : Text[..slash];

			return deathSources.TryGetValue(source, out var translated)
				? translated
				: source.Length > 0 ? Phrase.From(source) : Phrase.From("неизвестно");
		}
	}

	/// <summary>
	/// Ключ события между опросами. <c>seq</c> начинается с нуля при каждом старте сервера,
	/// поэтому в ключ входят время и игрок.
	/// </summary>
	public string Key => $"{At}:{Seq}:{Kind}:{Player}";

	/// <summary>Наблюдатель видит ленту, но не идентификаторы учётных записей игроков.</summary>
	public GameEvent Masked() => this with { Uid = string.Empty };
}

/// <summary>Фильтр ленты: вид и подстрока в тексте или имени игрока.</summary>
public sealed record GameEventFilter
{
	private const int maxSearch = 120;

	private GameEventFilter(string? kind, string needle)
	{
		Kind = kind;
		Needle = needle;
	}

	/// <summary><c>null</c> означает все виды.</summary>
	public string? Kind { get; }

	private string Needle { get; }

	/// <summary>Проверка фильтра; тексты отказов для интерфейса.</summary>
	public static Result<GameEventFilter> Create(string? kind, string? search)
	{
		var value = kind ?? "all";
		if (value != "all" && !GameEvent.Kinds.Contains(value, StringComparer.Ordinal))
		{
			return Failure.Invalid("Недопустимый тип события");
		}

		var text = search ?? string.Empty;
		if (text.EnumerateRunes().Count() > maxSearch)
		{
			return Failure.Invalid("Поисковая строка слишком длинная");
		}

		return new GameEventFilter(value == "all" ? null : value, text.Trim().ToLowerInvariant());
	}

	public IReadOnlyList<GameEvent> Apply(IEnumerable<GameEvent> events) =>
	[
		.. events.Where(item =>
			(Kind is null || item.Kind == Kind)
			&& (Needle.Length == 0
				|| item.Text.ToLowerInvariant().Contains(Needle, StringComparison.Ordinal)
				|| item.Player.ToLowerInvariant().Contains(Needle, StringComparison.Ordinal))),
	];
}
