using System.Globalization;
using Panel.SharedKernel;

namespace Panel.Runtime.Domain;

/// <summary>
/// Положение в журнале. На двух платформах оно разное: непрозрачная строка journald или
/// смещение в байтах в файле консоли.
/// </summary>
/// <remarks>
/// Отдельные типы не дают перепутать их: смещение, переданное journald, в лучшем случае
/// оказалось бы «недопустимым курсором». Разбор зависит от раскладки, как и проверка
/// абсолютности пути.
/// </remarks>
public abstract record LogCursor
{
	private LogCursor()
	{
	}

	/// <summary>Только то, что появится дальше.</summary>
	public sealed record Tail : LogCursor
	{
		public override string Serialize() => "tail";
	}

	/// <summary>Курсор journald. Содержимое непрозрачно, проверяется только форма.</summary>
	public sealed record Journald(string Cursor) : LogCursor
	{
		public override string Serialize() => Cursor;
	}

	/// <summary>Смещение в байтах в консольном журнале supervised-бэкенда.</summary>
	public sealed record ByteOffset(long Offset) : LogCursor
	{
		public override string Serialize() => Offset.ToString(CultureInfo.InvariantCulture);
	}

	public abstract string Serialize();

	/// <summary>
	/// Допустимые символы курсора journald: курсор передаётся аргументом внешней команде.
	/// </summary>
	private static bool IsSafeJournaldCursor(string value) =>
		value.Length is > 0 and <= 512
		&& value.All(symbol => char.IsAsciiLetterOrDigit(symbol) || symbol is ';' or ':' or '=' or '-' or '_');

	public static Result<LogCursor> Parse(string? raw, PlatformLayout layout)
	{
		var value = (raw ?? string.Empty).Trim();
		if (value.Length == 0 || value == "tail")
		{
			return new Tail();
		}

		if (layout is PlatformLayout.Windows)
		{
			return long.TryParse(value, CultureInfo.InvariantCulture, out var offset) && offset >= 0
				? new ByteOffset(offset)
				: Failure.Invalid("Недопустимое смещение в журнале");
		}

		return IsSafeJournaldCursor(value)
			? new Journald(value)
			: Failure.Invalid("Недопустимый курсор журнала");
	}
}

/// <summary>Порция журнала и место, с которого продолжать.</summary>
public sealed record LogPage(IReadOnlyList<LogEntry> Entries, LogCursor Next);

/// <summary>
/// Разобранная строка журнала: интерфейс раскрашивает её по уровню, а для наблюдателя из неё
/// удаляются адреса.
/// </summary>
/// <remarks>
/// <see cref="Line"/> — та же запись в формате <c>journalctl --output=short-iso</c>: метка
/// времени, узел, <c>ident[pid]:</c>, сообщение. По этой строке работают классификация и разбор
/// времени, и фронтенд показывает её как есть. Сообщение без префикса для этого не подходит:
/// строку <c>Started … Vintage Story Server</c> от systemd нельзя было бы отличить от строки игры.
/// </remarks>
public sealed record LogEntry(
	DateTimeOffset At,
	string Message,
	int Priority,
	string Source,
	string Line);
