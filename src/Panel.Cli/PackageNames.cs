using System.Text.RegularExpressions;

namespace Panel.Cli;

/// <summary>
/// Пакеты с номером версии в имени (<c>libicu74</c>, <c>libssl3t64</c>): берётся самый новый из
/// доступных.
/// </summary>
internal static partial class PackageNames
{
	public static Regex Icu => IcuPattern;

	public static Regex Ssl => SslPattern;

	public static string? Newest(IEnumerable<string> names, Regex pattern)
	{
		ArgumentNullException.ThrowIfNull(names);
		ArgumentNullException.ThrowIfNull(pattern);
		return names.Where(name => pattern.IsMatch(name)).OrderBy(name => name, NaturalOrder.Instance).LastOrDefault();
	}

	[GeneratedRegex(@"\Alibicu[0-9]+\z", RegexOptions.CultureInvariant)]
	private static partial Regex IcuPattern { get; }

	[GeneratedRegex(@"\Alibssl3(t64)?\z", RegexOptions.CultureInvariant)]
	private static partial Regex SslPattern { get; }
}
