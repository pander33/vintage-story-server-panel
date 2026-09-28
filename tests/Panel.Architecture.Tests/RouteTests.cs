using System.Text.RegularExpressions;

namespace Panel.Architecture.Tests;

/// <summary>
/// Встроенный фронтенд зовёт только маршруты, которые есть в <c>Panel.Web/Endpoints</c>.
/// </summary>
/// <remarks>
/// <para>
/// Компилятор расхождений адресов не видит: адрес для него просто строка. Маршруты
/// добавляются, переименовываются и удаляются вместе с фронтендом (ADR-18, ADR-19), и этот
/// тест ловит адрес, забытый на одной из сторон.
/// </para>
/// <para>
/// Маршруты берутся из исходников, а не из запущенного приложения: без <c>secret.key</c> и
/// демона его не поднять.
/// </para>
/// </remarks>
public sealed partial class RouteTests
{
	[Fact]
	public void Маршруты_найдены_в_исходниках() =>
		Assert.True(ImplementedRoutes().Count > 40, "Разбор исходников маршрутов ничего не нашёл");

	/// <summary>
	/// Адрес в шаблонной строке (<c>`/api/accounts/${name}`</c>) обрезается на подстановке и
	/// сверяется по префиксу.
	/// </summary>
	[Fact]
	public void Фронтенд_зовёт_только_существующие_маршруты()
	{
		var paths = ImplementedRoutes().Select(route => route[(route.IndexOf(' ') + 1)..]).ToList();
		var calls = Directory.EnumerateFiles(Path.Combine(Root(), "src", "Panel.Web", "Static"), "*.js")
			.SelectMany(file => FrontendCall().Matches(File.ReadAllText(file)))
			.Select(match => match.Groups["path"].Value)
			.Distinct(StringComparer.Ordinal)
			.ToList();

		var unknown = calls
			.Where(call => !paths.Any(path => path == call || (call.EndsWith('/') && path.StartsWith(call, StringComparison.Ordinal))))
			.ToList();

		Assert.True(calls.Count > 50, "Разбор фронтенда ничего не нашёл");
		Assert.True(unknown.Count == 0, "Фронтенд зовёт несуществующие адреса: " + string.Join(", ", unknown));
	}

	private static List<string> ImplementedRoutes()
	{
		var directory = Path.Combine(Root(), "src", "Panel.Web", "Endpoints");

		return [.. Directory.EnumerateFiles(directory, "*.cs")
			.SelectMany(file => MapCall().Matches(File.ReadAllText(file)))
			.Select(match => $"{match.Groups["verb"].Value.ToUpperInvariant()} {match.Groups["path"].Value}")
			.Distinct(StringComparer.Ordinal)];
	}

	private static string Root()
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			if (File.Exists(Path.Combine(directory.FullName, "VintageStoryPanel.slnx")))
			{
				return directory.FullName;
			}
		}

		throw new InvalidOperationException("Не найден корень репозитория (VintageStoryPanel.slnx)");
	}

	[GeneratedRegex(@"\.Map(?<verb>Get|Post|Put|Delete|Patch)\(\s*""(?<path>[^""]+)""")]
	private static partial Regex MapCall();

	[GeneratedRegex(@"[""'`](?<path>/api/[^""'`?$]*)")]
	private static partial Regex FrontendCall();
}
