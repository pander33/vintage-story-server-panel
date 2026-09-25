using System.Text.RegularExpressions;

namespace Panel.Architecture.Tests;

/// <summary>
/// Каждый маршрут есть в замороженном списке (ADR-16) с тем же методом и адресом или явно
/// назван в списке добавлений 2.0 (ADR-17).
/// </summary>
/// <remarks>
/// <para>
/// Компилятор расхождений адресов не видит: адрес для него просто строка, а фронтенд
/// вызывает конкретные адреса.
/// </para>
/// <para>
/// Маршруты берутся из исходников <c>Panel.Web/Endpoints</c>, а не из запущенного приложения:
/// без <c>secret.key</c> и демона его не поднять. Форму ответов проверяют контрактные тесты и
/// золотые фикстуры.
/// </para>
/// </remarks>
public sealed partial class RouteParityTests
{
	[Fact]
	public void Каждый_маршрут_есть_в_замороженном_списке_или_в_добавлениях_2_0()
	{
		var known = KnownRoutes();
		var missing = ImplementedRoutes().Where(route => !known.Contains(route)).ToList();

		Assert.True(missing.Count == 0, "Маршрутов нет ни в замороженном списке, ни в добавлениях 2.0: " + string.Join(", ", missing));
	}

	/// <summary>Защита от пустой или обрезанной фикстуры, с которой проверка выше ничего бы не проверяла.</summary>
	[Fact]
	public void Фикстура_маршрутов_полна() =>
		Assert.Equal(106, FrozenRoutes().Count);

	/// <summary>Все замороженные маршруты на месте: добавлять можно, убирать нельзя (ADR-17).</summary>
	[Fact]
	public void Замороженные_маршруты_не_убраны()
	{
		var implemented = ImplementedRoutes().ToHashSet(StringComparer.Ordinal);
		var gone = FrozenRoutes().Where(route => !implemented.Contains(route)).ToList();

		Assert.True(gone.Count == 0, "Замороженные маршруты пропали: " + string.Join(", ", gone));
	}

	/// <summary>Добавленный маршрут не может совпадать с замороженным.</summary>
	[Fact]
	public void Добавления_2_0_не_пересекаются_с_замороженными_маршрутами() =>
		Assert.Empty(AddedRoutes().Intersect(FrozenRoutes()));

	[Fact]
	public void Маршруты_найдены_в_исходниках() =>
		Assert.True(ImplementedRoutes().Count > 40, "Разбор исходников маршрутов ничего не нашёл");

	/// <summary>
	/// Встроенный фронтенд вызывает только известные адреса. Адрес в шаблонной строке
	/// (<c>`/api/accounts/${name}`</c>) обрезается на подстановке и сверяется по префиксу.
	/// </summary>
	[Fact]
	public void Фронтенд_зовёт_только_известные_адреса()
	{
		var frozen = KnownRoutes().Select(route => route[(route.IndexOf(' ') + 1)..]).ToList();
		var calls = Directory.EnumerateFiles(Path.Combine(Root(), "src", "Panel.Web", "Static"), "*.js")
			.SelectMany(file => FrontendCall().Matches(File.ReadAllText(file)))
			.Select(match => match.Groups["path"].Value)
			.Distinct(StringComparer.Ordinal)
			.ToList();

		var unknown = calls
			.Where(call => !frozen.Any(path => path == call || (call.EndsWith('/') && path.StartsWith(call, StringComparison.Ordinal))))
			.ToList();

		Assert.True(calls.Count > 50, "Разбор фронтенда ничего не нашёл");
		Assert.True(unknown.Count == 0, "Фронтенд зовёт неизвестные адреса: " + string.Join(", ", unknown));
	}

	private static HashSet<string> FrozenRoutes() => ReadRoutes("routes-1.2.x.txt");

	private static HashSet<string> AddedRoutes() => ReadRoutes("routes-2.0.txt");

	private static HashSet<string> KnownRoutes() => [.. FrozenRoutes(), .. AddedRoutes()];

	private static HashSet<string> ReadRoutes(string name) =>
	[
		.. File.ReadAllLines(Path.Combine(Root(), "fixtures", name))
			.Select(line => line.Trim())
			.Where(line => line.Length > 0 && !line.StartsWith('#')),
	];

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
