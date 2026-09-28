using System.Reflection;
using System.Text.RegularExpressions;
using Panel.Web.Application;

namespace Panel.Architecture.Tests;

/// <summary>
/// Запрет менять сервер во время обслуживания стоит ровно на тех маршрутах, где он нужен.
/// </summary>
/// <remarks>
/// Метку <see cref="MutatesServerAttribute"/> на типе команды легко забыть или поставить
/// лишнюю, и компилятор этого не заметит: в первом случае сервер можно будет менять во время
/// остановки на копию, во втором появится лишний 409. Поэтому соответствие со списком
/// префиксов проверяется по исходникам маршрутов в обе стороны.
/// </remarks>
public sealed partial class ServerMutationTests
{
	/// <summary>Префиксы адресов, которые меняют сервер.</summary>
	private static readonly string[] prefixes =
	[
		"/api/service", "/api/config", "/api/worlds", "/api/mods", "/api/backups",
		"/api/world-config", "/api/mod-configs", "/api/updates", "/api/server",
	];

	[Fact]
	public void Метка_стоит_ровно_на_маршрутах_с_известным_префиксом()
	{
		var routes = MutatingRoutes();
		Assert.NotEmpty(routes);

		var wrong = routes
			.Where(route => prefixes.Any(prefix => route.Path.StartsWith(prefix, StringComparison.Ordinal))
							!= Marked(route.Command))
			.Select(route => $"{route.Path} ({route.Command})")
			.ToList();

		Assert.True(wrong.Count == 0, "Метка расходится со списком префиксов: " + string.Join(", ", wrong));
	}

	private static bool Marked(string command)
	{
		var type = typeof(MutatesServerAttribute).Assembly.GetTypes().SingleOrDefault(item => item.Name == command);
		Assert.True(type is not null, $"Команда {command} не найдена в Panel.Web");
		return type!.IsDefined(typeof(MutatesServerAttribute), inherit: false);
	}

	/// <summary>Изменяющий маршрут и первая команда, которую он авторизует.</summary>
	private static List<(string Path, string Command)> MutatingRoutes()
	{
		var directory = Path.Combine(Root(), "src", "Panel.Web", "Endpoints");
		var result = new List<(string, string)>();

		foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
		{
			var text = File.ReadAllText(file);
			var maps = MutatingMap().Matches(text).ToList();
			for (var index = 0; index < maps.Count; index++)
			{
				var end = index + 1 < maps.Count ? maps[index + 1].Index : text.Length;
				var body = text[maps[index].Index..end];
				var command = CommandType().Match(body);
				if (command.Success)
				{
					result.Add((maps[index].Groups["path"].Value, command.Groups["type"].Value));
				}
			}
		}

		return result;
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

		throw new InvalidOperationException("Корень решения не найден");
	}

	[GeneratedRegex("""Map(?:Post|Put|Delete)\(\s*"(?<path>/api/[^"]*)""")]
	private static partial Regex MutatingMap();

	[GeneratedRegex(@"typeof\((?<type>[A-Za-z]+)\)")]
	private static partial Regex CommandType();
}
