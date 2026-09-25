using System.Globalization;
using System.Text.RegularExpressions;
using Panel.SharedKernel;

namespace Panel.Observability.Domain;

/// <summary>Категория строки журнала — фильтр вкладки «Журнал».</summary>
public enum LogCategory
{
	Error,
	Player,
	Mod,
	System,
}

public static class LogCategoryWire
{
	public static string ToWire(LogCategory category) => category switch
	{
		LogCategory.Error => "error",
		LogCategory.Player => "player",
		LogCategory.Mod => "mod",
		_ => "system",
	};
}

/// <summary>Вход или выход игрока, узнанный по строке журнала.</summary>
public sealed record PlayerChange(bool Joined, string Player)
{
	public string Type => Joined ? "join" : "leave";
}

/// <summary>
/// Строка журнала и то, что о ней известно: категория, событие игрока, перегрузка, время.
/// </summary>
/// <remarks>
/// Классифицируется вся строка <c>short-iso</c>, а не только сообщение: часть признаков —
/// строки systemd (<c>failed with result</c>, <c>Started … Server</c>), и отличить их от строк
/// игры можно только по префиксу.
/// </remarks>
public sealed partial record LogEvent(
	string Line,
	LogCategory Category,
	PlayerChange? PlayerChange,
	bool Overload,
	string? Cursor,
	long? Timestamp)
{
	public static LogEvent FromLine(string line, string? cursor = null)
	{
		ArgumentNullException.ThrowIfNull(line);

		return new LogEvent(
			line,
			Classify(line),
			PlayerChangeIn(line),
			Overloaded().IsMatch(line),
			cursor,
			TimestampOf(line));
	}

	/// <summary>Та же запись с другой строкой; нужна для сокрытия адресов после классификации.</summary>
	public LogEvent WithLine(Func<string, string> rewrite)
	{
		ArgumentNullException.ThrowIfNull(rewrite);
		return this with { Line = rewrite(Line) };
	}

	private static LogCategory Classify(string line)
	{
		if (Error().IsMatch(line))
		{
			return LogCategory.Error;
		}

		if (Player().IsMatch(line))
		{
			return LogCategory.Player;
		}

		return Mod().IsMatch(line) ? LogCategory.Mod : LogCategory.System;
	}

	private static PlayerChange? PlayerChangeIn(string line)
	{
		foreach (var pattern in new[] { JoinWithAddress(), Join() })
		{
			var match = pattern.Match(line);
			if (match.Success)
			{
				return new PlayerChange(true, match.Groups["name"].Value);
			}
		}

		foreach (var pattern in new[] { Left(), Disconnected() })
		{
			var match = pattern.Match(line);
			if (match.Success)
			{
				return new PlayerChange(false, match.Groups["name"].Value);
			}
		}

		return null;
	}

	/// <summary>
	/// Метка времени в начале строки <c>short-iso</c>; смещение принимается и с двоеточием, и без.
	/// </summary>
	private static long? TimestampOf(string line)
	{
		var space = line.IndexOf(' ', StringComparison.Ordinal);
		var head = space < 0 ? line : line[..space];
		if (head.Length < 19)
		{
			return null;
		}

		var normalized = CompactOffset().Replace(head, "$1$2:$3");

		return DateTimeOffset.TryParseExact(
			normalized,
			["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
			CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeLocal,
			out var moment)
			? moment.ToUnixTimeSeconds()
			: null;
	}

	[GeneratedRegex(
		@"\[(?:error|critical)\]|\b(?:fatal|unhandled exception|critical error|segmentation fault|core dump|" +
		@"too many errors detected|main process exited|failed with result)\b",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex Error();

	[GeneratedRegex(
		@"(?:\bplayer\b|\bclient\s+\d+\b|\bjoins\.|\bleft\.|\bdisconnected\b|" +
		@"\battempting identification\b|\bwhitelist\b|\bbanned?\b)",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex Player();

	[GeneratedRegex(
		@"\b(?:mod|mods|modsystem|modinfo|patch(?:es|ing)?|loaded assembly|instantiate mod systems)\b|\[[a-z0-9_.-]+\]",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex Mod();

	[GeneratedRegex(
		@"server overloaded|server is overloaded|skipping \d+ tick|took too long",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex Overloaded();

	[GeneratedRegex(@"(?:\]|\s)(?<name>[A-Za-z0-9_-]{1,32})\s+\[[^\]]+\]:\d+\s+joins\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex JoinWithAddress();

	[GeneratedRegex(@"(?:\]|\s)(?<name>[A-Za-z0-9_-]{1,32})\s+joins\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex Join();

	[GeneratedRegex(@"\bPlayer\s+(?<name>[A-Za-z0-9_-]{1,32})\s+(?:left\.|got removed\.)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex Left();

	[GeneratedRegex(@"\bUDP:\s*client disconnected\s+(?<name>[A-Za-z0-9_-]{1,32})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex Disconnected();

	[GeneratedRegex(@"^(.*\d{2}:\d{2}:\d{2}(?:\.\d+)?)([+-]\d{2})(\d{2})$")]
	private static partial Regex CompactOffset();
}

/// <summary>Фильтр вкладки «Журнал»: категория и подстрока без учёта регистра.</summary>
public sealed record LogFilter
{
	private const int maxSearch = 120;

	private LogFilter(LogCategory? category, string needle)
	{
		Category = category;
		Needle = needle;
	}

	/// <summary><c>null</c> означает все категории.</summary>
	public LogCategory? Category { get; }

	private string Needle { get; }

	public static LogFilter All { get; } = new(null, string.Empty);

	/// <summary>Проверка фильтра; тексты отказов для интерфейса.</summary>
	public static Result<LogFilter> Create(string? category, string? search)
	{
		LogCategory? parsed;
		switch (category ?? "all")
		{
			case "all": parsed = null; break;
			case "error": parsed = LogCategory.Error; break;
			case "player": parsed = LogCategory.Player; break;
			case "mod": parsed = LogCategory.Mod; break;
			case "system": parsed = LogCategory.System; break;
			default: return Failure.Invalid("Неизвестная категория журнала");
		}

		var text = search ?? string.Empty;
		if (text.EnumerateRunes().Count() > maxSearch)
		{
			return Failure.Invalid("Поисковая строка слишком длинная");
		}

		return new LogFilter(parsed, text.Trim().ToLowerInvariant());
	}

	public IReadOnlyList<LogEvent> Apply(IEnumerable<LogEvent> events) =>
	[
		.. events.Where(item =>
			(Category is null || item.Category == Category)
			&& (Needle.Length == 0 || item.Line.ToLowerInvariant().Contains(Needle, StringComparison.Ordinal))),
	];
}

/// <summary>
/// Сводка сбоев: сколько отдельных сбоев в окне журнала и повторяются ли они.
/// </summary>
/// <remarks>
/// Строки одного сбоя идут подряд: трассировка, «main process exited», «failed with result».
/// Совпадения ближе пяти строк друг к другу считаются одним сбоем, и его строкой становится
/// последняя.
/// </remarks>
public sealed partial record CrashSummary(int Count, bool Repeated, string? Last)
{
	private const int sameIncidentWithin = 5;
	private const int repeatedFrom = 3;

	public static CrashSummary Of(IReadOnlyList<LogEvent> events)
	{
		ArgumentNullException.ThrowIfNull(events);

		var incidents = new List<string>();
		var previous = -100;

		for (var index = 0; index < events.Count; index++)
		{
			if (!Crash().IsMatch(events[index].Line))
			{
				continue;
			}

			if (index - previous > sameIncidentWithin)
			{
				incidents.Add(events[index].Line);
			}
			else
			{
				incidents[^1] = events[index].Line;
			}

			previous = index;
		}

		return new CrashSummary(incidents.Count, incidents.Count >= repeatedFrom, incidents.Count > 0 ? incidents[^1] : null);
	}

	[GeneratedRegex(
		@"(?:critical error|unhandled exception|segmentation fault|core dump|too many errors detected|" +
		@"main process exited|failed with result|server shutting down.*errors)",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	internal static partial Regex Crash();
}

/// <summary>Игрок, который по журналу сейчас на сервере, и строка его входа.</summary>
public sealed record OnlinePlayer(string Name, string Since);

/// <summary>
/// Кто на сервере, если телеметрии нет: по входам и выходам в журнале.
/// </summary>
/// <remarks>
/// Строка начала сессии сервера сбрасывает список: игроки прошлого процесса не остаются
/// онлайн после перезапуска, даже если строки их выхода нет.
/// </remarks>
public static partial class OnlinePlayers
{
	public static IReadOnlyList<OnlinePlayer> Infer(IEnumerable<LogEvent> events)
	{
		ArgumentNullException.ThrowIfNull(events);

		var online = new Dictionary<string, OnlinePlayer>(StringComparer.Ordinal);
		foreach (var item in events)
		{
			if (SessionReset().IsMatch(item.Line))
			{
				online.Clear();
			}

			if (item.PlayerChange is not { } change)
			{
				continue;
			}

			var key = change.Player.ToLowerInvariant();
			if (change.Joined)
			{
				online[key] = new OnlinePlayer(change.Player, item.Line);
			}
			else
			{
				online.Remove(key);
			}
		}

		return [.. online.Values.OrderBy(player => player.Name.ToLowerInvariant(), StringComparer.Ordinal)];
	}

	[GeneratedRegex(
		@"(?:Dedicated Server now running|Entering runphase RunGame|Started Vintage Story.*Server)",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex SessionReset();
}
