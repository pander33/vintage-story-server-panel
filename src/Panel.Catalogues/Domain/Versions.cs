using System.Globalization;
using System.Text.RegularExpressions;

namespace Panel.Catalogues.Domain;

/// <summary>
/// Версия мода или зависимости.
/// </summary>
/// <remarks>
/// <para>
/// Версия разбирается тремя способами с разными допусками (детализация Catalogues §1): этот,
/// ветка тега ModDB (<see cref="VersionBranch"/>) и номер пакета в манифесте
/// (<see cref="ManifestVersion"/>). Если их объединить, изменится, какие теги совместимы и
/// какие зависимости «не проверены». Поэтому разборы остаются отдельными, а расхождения
/// между ними записаны в дифференциальном тесте.
/// </para>
/// <para>
/// Порядок как у выпусков Vintage Story: dev и alpha раньше pre, preview и beta, те раньше
/// rc, а релиз позже любого своего предрелиза.
/// </para>
/// </remarks>
public readonly partial record struct ModVersion : IComparable<ModVersion>
{
	private static readonly Dictionary<string, int> ranks = new(StringComparer.Ordinal)
	{
		["dev"] = 0,
		["alpha"] = 0,
		["pre"] = 1,
		["preview"] = 1,
		["beta"] = 1,
		["rc"] = 2,
	};

	private ModVersion(long major, long minor, long patch, int rank, long number)
	{
		Major = major;
		Minor = minor;
		Patch = patch;
		Rank = rank;
		Number = number;
	}

	public long Major { get; }

	public long Minor { get; }

	public long Patch { get; }

	/// <summary>0 — dev/alpha, 1 — pre/preview/beta, 2 — rc, 3 — релиз.</summary>
	public int Rank { get; }

	public long Number { get; }

	public bool IsPrerelease => Rank < 3;

	/// <summary>Только цифры ASCII, без цифр других письменностей.</summary>
	[GeneratedRegex(
		@"\Av?([0-9]+)\.([0-9]+)(?:\.([0-9]+))?(?:-(dev|pre|preview|rc|alpha|beta)(?:[.-]?([0-9]+))?)?(?:\+.*)?\z",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex Pattern { get; }

	/// <summary><c>null</c>, если строку нельзя сравнивать; такая зависимость «не проверена».</summary>
	public static ModVersion? Parse(string? raw)
	{
		var match = Pattern.Match((raw ?? string.Empty).Trim());
		if (!match.Success
			|| !ReadNumber(match.Groups[1], out var major)
			|| !ReadNumber(match.Groups[2], out var minor)
			|| !ReadNumber(match.Groups[3], out var patch)
			|| !ReadNumber(match.Groups[5], out var number))
		{
			return null;
		}

		var kind = match.Groups[4].Value.ToLowerInvariant();
		return new ModVersion(major, minor, patch, ranks.GetValueOrDefault(kind, 3), number);
	}

	public int CompareTo(ModVersion other)
	{
		var order = Major.CompareTo(other.Major);
		if (order == 0) order = Minor.CompareTo(other.Minor);
		if (order == 0) order = Patch.CompareTo(other.Patch);
		if (order == 0) order = Rank.CompareTo(other.Rank);
		return order != 0 ? order : Number.CompareTo(other.Number);
	}

	public static bool operator <(ModVersion left, ModVersion right) => left.CompareTo(right) < 0;

	public static bool operator >(ModVersion left, ModVersion right) => left.CompareTo(right) > 0;

	public static bool operator <=(ModVersion left, ModVersion right) => left.CompareTo(right) <= 0;

	public static bool operator >=(ModVersion left, ModVersion right) => left.CompareTo(right) >= 0;

	/// <summary>Отсутствующая группа даёт ноль, как <c>int(patch or 0)</c>.</summary>
	private static bool ReadNumber(Group group, out long value)
	{
		value = 0;
		return !group.Success || long.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out value);
	}
}

/// <summary>
/// Ветка совместимости major.minor по тегу ModDB.
/// </summary>
/// <remarks>
/// Самый нестрогий из трёх разборов: теги ModDB не следуют semver (<c>v1.20</c>,
/// <c>1.20.0_rc1</c>, разделители <c>-</c> или <c>+</c>). Номера сравниваются как текст,
/// поэтому <c>01.22</c> и <c>1.22</c> — разные ветки.
/// </remarks>
public readonly partial record struct VersionBranch
{
	private VersionBranch(string major, string minor)
	{
		Major = major;
		Minor = minor;
	}

	public string Major { get; }

	public string Minor { get; }

	[GeneratedRegex(@"\Av?([0-9]+)\.([0-9]+)(?:\.[0-9]+)?(?:[-+].*)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex Pattern { get; }

	public static VersionBranch? Parse(string? raw)
	{
		var match = Pattern.Match(raw ?? string.Empty);
		return match.Success ? new VersionBranch(match.Groups[1].Value, match.Groups[2].Value) : null;
	}

	public override string ToString() => $"{Major}.{Minor}";
}

/// <summary>
/// Версия игры, под которую панель подбирает моды: <c>game_version</c> сервера из реестра
/// или <c>PANEL_GAME_VERSION</c>.
/// </summary>
/// <remarks>
/// Разбор не обязателен: с версией «latest» или опечаткой в реестре панель работает, просто
/// совместимым считается только тег, совпавший дословно.
/// </remarks>
public sealed record GameVersion
{
	private GameVersion(string text)
	{
		Text = text;
		Branch = VersionBranch.Parse(text);
	}

	/// <summary>Исходный текст; так же возвращается в ответах (<c>game_version</c>).</summary>
	public string Text { get; }

	public VersionBranch? Branch { get; }

	/// <summary>Снимается только строчный префикс <c>v</c>, как <c>removeprefix("v")</c>.</summary>
	internal string Normalized => Unprefixed(Text);

	public static GameVersion Create(string text) => new(text ?? string.Empty);

	internal static string Unprefixed(string value) =>
		value.StartsWith('v') ? value[1..] : value;

	public override string ToString() => Text;
}

/// <summary>
/// Номер серверного пакета из официального манифеста.
/// </summary>
/// <remarks>
/// Строже двух других разборов: ровно три числа и короткий суффикс. Манифест пишут
/// разработчики игры, и запись другого вида пропускается.
/// </remarks>
public readonly partial record struct ManifestVersion : IComparable<ManifestVersion>
{
	private readonly long[] numbers;

	private ManifestVersion(string text, long[] numbers, string suffix)
	{
		Text = text;
		this.numbers = numbers;
		Suffix = suffix;
	}

	public string Text { get; }

	/// <summary>Всё после первого <c>-</c>; у релиза пусто.</summary>
	public string Suffix { get; }

	/// <summary>Нестабильная версия по маркеру предрелиза.</summary>
	public bool IsUnstable => Unstable.IsMatch(Text);

	[GeneratedRegex(@"\A[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,4}(?:-[A-Za-z0-9.]{1,16})?\z", RegexOptions.CultureInvariant)]
	private static partial Regex Pattern { get; }

	[GeneratedRegex(@"[-.](?:dev|pre|preview|rc|alpha|beta)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex Unstable { get; }

	public static ManifestVersion? Parse(string? raw)
	{
		if (raw is null || !Pattern.IsMatch(raw))
		{
			return null;
		}

		var dash = raw.IndexOf('-', StringComparison.Ordinal);
		var head = dash < 0 ? raw : raw[..dash];
		var numbers = head.Split('.')
			.Select(part => long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
			.Concat(Enumerable.Repeat(0L, 4))
			.Take(4)
			.ToArray();

		return new ManifestVersion(raw, numbers, dash < 0 ? string.Empty : raw[(dash + 1)..]);
	}

	/// <summary>Релиз идёт после своих предрелизов: 1.22.0 &gt; 1.22.0-rc.1.</summary>
	public int CompareTo(ManifestVersion other)
	{
		for (var index = 0; index < 4; index++)
		{
			var order = numbers[index].CompareTo(other.numbers[index]);
			if (order != 0)
			{
				return order;
			}
		}

		var released = Suffix.Length == 0 ? 1 : 0;
		var otherReleased = other.Suffix.Length == 0 ? 1 : 0;
		return released != otherReleased
			? released.CompareTo(otherReleased)
			: string.CompareOrdinal(Suffix, other.Suffix);
	}

	public override string ToString() => Text;
}
