using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Panel.Contracts;
using Panel.Contracts.Agent;
using Panel.Observability.Domain;
using Panel.SharedKernel;
using Panel.Web.Application;
using Panel.Web.Endpoints;

namespace Panel.Web.Tests;

/// <summary>
/// Перевод сообщений панели: каталог <c>Resources/phrases-en.json</c> полон и не устарел, а
/// граница HTTP переводит отказ на язык запроса.
/// </summary>
public sealed partial class PhraseCatalogTests
{
	[Fact]
	public void Интерполированная_строка_становится_шаблоном()
	{
		var port = 42420;
		var failure = Failure.Conflict($"Порт {port} уже занят сервером {"main"}");

		Assert.Equal("Порт {0} уже занят сервером {1}", failure.Text.Template);
		Assert.Equal("Порт 42420 уже занят сервером main", failure.Message);
	}

	[Fact]
	public void Фигурные_скобки_текста_не_становятся_местами()
	{
		var failure = Failure.Invalid("Ожидался объект {…}");

		Assert.Equal("Ожидался объект {{…}}", failure.Text.Template);
		Assert.Equal("Ожидался объект {…}", failure.Message);
	}

	[Fact]
	public void Отказ_переводится_на_язык_запроса()
	{
		var failure = Failure.Conflict($"Порт {42420} уже занят сервером {"main"}");

		Assert.Equal("Port 42420 is already used by server main", PhraseCatalog.Translate(failure.Text, PanelLanguage.English));
		Assert.Equal("Порт 42420 уже занят сервером main", PhraseCatalog.Translate(failure.Text, PanelLanguage.Russian));
	}

	[Fact]
	public void Вложенный_отказ_переводится_вместе_с_внешним()
	{
		var inner = Failure.Unavailable($"Реестр повреждён: {"unexpected end"}");
		var outer = Failure.Unavailable($"Не удалось записать реестр: {inner}");

		Assert.Equal("Could not write the registry: The registry is corrupted: unexpected end", PhraseCatalog.Translate(outer.Text, PanelLanguage.English));
	}

	[Fact]
	public void Неизвестный_шаблон_остаётся_русским_целиком()
	{
		Phrase phrase = Phrase.Format($"Такой фразы нет в каталоге: {"Сервер не найден"}");

		Assert.Equal("Такой фразы нет в каталоге: Сервер не найден", PhraseCatalog.Translate(phrase, PanelLanguage.English));
	}

	[Fact]
	public void Фраза_переживает_протокол_демона()
	{
		var sent = AgentReply.Failure("conflict", $"Порт {42420} уже занят сервером {"main"}");
		var reply = JsonSerializer.Deserialize<AgentReply>(JsonSerializer.Serialize(sent, PanelJson.Options), PanelJson.Options)!;

		Assert.Equal("Порт 42420 уже занят сервером main", reply.Error);
		Assert.Equal("Port 42420 is already used by server main", PhraseCatalog.Translate(reply.FailureText(Phrase.From("?")), PanelLanguage.English));
	}

	[Theory]
	[InlineData(null, PanelLanguage.English)]
	[InlineData("en", PanelLanguage.English)]
	[InlineData("ru", PanelLanguage.Russian)]
	public void Язык_берётся_из_cookie(string? cookie, PanelLanguage expected)
	{
		var http = new DefaultHttpContext();
		if (cookie is not null)
		{
			http.Request.Headers.Cookie = $"{LocalizedJson.CookieName}={cookie}";
		}

		Assert.Equal(expected, LocalizedJson.LanguageOf(http));
	}

	[Fact]
	public async Task Ответ_отказа_на_английском()
	{
		var http = new DefaultHttpContext();
		http.Response.Body = new MemoryStream();
		http.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();

		await PanelResults.Refuse(Failure.NotFound("Пользователь не найден")).ExecuteAsync(http);

		http.Response.Body.Position = 0;
		Assert.Equal(StatusCodes.Status404NotFound, http.Response.StatusCode);
		Assert.Contains("\"detail\":\"User not found\"", await new StreamReader(http.Response.Body).ReadToEndAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public void Фраза_хранится_шаблоном_и_значениями()
	{
		var phrase = Phrase.Format($"Ошибка перезапуска: {Failure.Unavailable($"Реестр повреждён: {"eof"}")}");

		var json = JsonSerializer.Serialize(phrase, PanelJson.Options);
		var read = JsonSerializer.Deserialize<Phrase>(json, PanelJson.Options)!;

		Assert.Equal("""{"$phrase":"Ошибка перезапуска: {0}","args":[{"$phrase":"Реестр повреждён: {0}","args":["eof"]}]}""", json);
		Assert.Equal(phrase, read);
		Assert.Equal("Restart failed: The registry is corrupted: eof", PhraseCatalog.Translate(read, PanelLanguage.English));
	}

	/// <summary>
	/// Файлы состояния, записанные до фраз, хранят готовый текст строкой: он читается как фраза без
	/// значений и переводится, если есть в каталоге дословно.
	/// </summary>
	[Fact]
	public void Строка_из_старого_файла_читается_как_фраза()
	{
		var read = PhraseJsonConverter.FromNode(JsonValue.Create("Сервер снова работает"))!;

		Assert.Equal("The server is running again", PhraseCatalog.Translate(read, PanelLanguage.English));
		Assert.Null(PhraseJsonConverter.FromNode(JsonValue.Create(42)));
	}

	[Theory]
	[InlineData(null, "The server is loading (12 s)", "Watchdog: The watchdog is disabled")]
	[InlineData("ru", "Сервер загружается (12 сек.)", "Watchdog: Watchdog отключён")]
	public async Task Ответ_переводит_фразы_и_внутри_готового_дерева(string? cookie, string detail, string title)
	{
		var http = new DefaultHttpContext();
		http.Response.Body = new MemoryStream();
		http.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
		if (cookie is not null)
		{
			http.Request.Headers.Cookie = $"{LocalizedJson.CookieName}={cookie}";
		}

		var seconds = 12;
		var events = new JsonArray(new JsonObject
		{
			["title"] = PhraseJsonConverter.ToNode(Phrase.Format($"Watchdog: {Phrase.From("Watchdog отключён")}")),
		});
		await PanelResults.Json(new { detail = Phrase.Format($"Сервер загружается ({seconds} сек.)"), events }).ExecuteAsync(http);

		http.Response.Body.Position = 0;
		var body = JsonNode.Parse(await new StreamReader(http.Response.Body).ReadToEndAsync())!;
		Assert.Equal(detail, (string?) body["detail"]);
		Assert.Equal(title, (string?) body["events"]![0]!["title"]);
	}

	[Fact]
	public void Уведомление_пишется_на_языке_настроек()
	{
		var record = new JsonObject
		{
			["severity"] = "error",
			["title"] = PhraseJsonConverter.ToNode(Phrase.From("Сервер остановлен")),
			["detail"] = PhraseJsonConverter.ToNode(Phrase.From(string.Empty)),
		};
		var settings = NotificationSettings.Defaults
			.Apply(new NotificationUpdate(true, "info", 300, "https://example.test/hook", null, null, null, "ru"))
			.Value;

		var delivery = Assert.Single(NotificationMessage.For(record, settings, PhraseCatalog.Renderer(PhraseCatalog.Parse(settings.Language))));

		Assert.Equal("ru", settings.Language);
		Assert.Equal("Сервер остановлен", (string?) delivery.Payload["title"]);
		Assert.Equal("en", NotificationSettings.Defaults.Language);
	}

	/// <summary>
	/// Каждый русский шаблон отказа в исходниках есть в каталоге. Новый отказ без перевода
	/// показался бы английскому интерфейсу по-русски.
	/// </summary>
	[Fact]
	public void Каталог_переводит_каждый_шаблон_из_исходников()
	{
		var missing = SourceTemplates().Keys.Where(template => !PhraseCatalog.English.ContainsKey(template)).Order(StringComparer.Ordinal).ToList();

		Assert.True(missing.Count == 0, "Нет перевода:\n" + string.Join("\n", missing.Select(template => $"  \"{template}\" ({SourceTemplates()[template]})")));
	}

	[Fact]
	public void В_каталоге_нет_шаблонов_которых_нет_в_исходниках()
	{
		var templates = SourceTemplates();
		var stale = PhraseCatalog.English.Keys.Where(template => !templates.ContainsKey(template)).Order(StringComparer.Ordinal).ToList();

		Assert.True(stale.Count == 0, "Шаблонов больше нет в исходниках:\n" + string.Join("\n", stale));
	}

	[Fact]
	public void Перевод_сохраняет_места_шаблона()
	{
		var broken = PhraseCatalog.English
			.Where(entry => !Holes(entry.Key).SetEquals(Holes(entry.Value)))
			.Select(entry => entry.Key)
			.ToList();

		Assert.Empty(broken);
	}

	/// <summary>
	/// Ответ пишет только <see cref="PanelResults"/>, поток — только <see cref="LocalizedJson"/>:
	/// <c>Results.Json</c> с <c>PanelJson.Options</c> или <c>ToJsonString</c> отдали бы фразу
	/// объектом <c>$phrase</c>, а не текстом.
	/// </summary>
	[Fact]
	public void Json_ответа_пишется_только_на_языке_запроса()
	{
		var web = Path.Combine(Root(), "src", "Panel.Web");
		var offenders = Directory.EnumerateFiles(web, "*.cs", SearchOption.AllDirectories)
			.Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
			.Where(file => Path.GetFileName(file) != "PanelResults.cs")
			.Where(file => ResponseWriter().IsMatch(File.ReadAllText(file))
				|| (file.Contains($"{Path.DirectorySeparatorChar}Endpoints{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
					&& StreamWriter().IsMatch(File.ReadAllText(file))))
			.Select(file => Path.GetRelativePath(Root(), file))
			.ToList();

		Assert.Empty(offenders);
	}

	/// <summary>
	/// <c>Failure.Message</c> — русская строка: присвоенная полю ответа, она не переводится. В ответ
	/// идёт <c>Failure.Text</c>; <c>Message</c> остаётся журналу, где его передают аргументом.
	/// </summary>
	[Fact]
	public void Текст_отказа_не_уходит_в_ответ_готовой_строкой()
	{
		var offenders = Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories)
			.Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
			.SelectMany(file => FailureMessage().Matches(File.ReadAllText(file)).Select(match => $"{Path.GetRelativePath(Root(), file)}: {match.Value}"))
			.ToList();

		Assert.Empty(offenders);
		Assert.Matches(FailureMessage(), "Detail = listing.Failure!.Message,");
		Assert.Matches(FailureMessage(), "Match<string?>(_ => null, failure => failure.Message)");
		Assert.DoesNotMatch(FailureMessage(), "EventLost(log, instance.Value, written.Failure!.Message);");
	}

	[Fact]
	public void Разбор_исходников_находит_шаблоны() =>
		Assert.True(SourceTemplates().Count > 300, "Разбор исходников ничего не нашёл");

	private static HashSet<string> Holes(string template) =>
		[.. Hole().Matches(template).Select(match => match.Value)];

	private static Dictionary<string, string>? sourceTemplates;

	/// <summary>
	/// Шаблоны из вызовов, принимающих <see cref="Phrase"/>: первый аргумент-текст — литерал,
	/// интерполированная строка или их сумма. Шаблон строится так же, как его строит
	/// <see cref="PhraseHandler"/> и <see cref="Phrase.From"/>. Учитываются только русские.
	/// </summary>
	private static Dictionary<string, string> SourceTemplates()
	{
		if (sourceTemplates is not null)
		{
			return sourceTemplates;
		}

		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories))
		{
			if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
			{
				continue;
			}

			var text = File.ReadAllText(file);
			foreach (Match call in PhraseCall().Matches(text))
			{
				var template = Template(text, call.Index + call.Length);
				if (template is not null && Cyrillic().IsMatch(template))
				{
					result.TryAdd(template, Path.GetRelativePath(Root(), file));
				}
			}
		}

		return sourceTemplates = result;
	}

	/// <summary>Шаблон выражения с позиции <paramref name="index"/> или null, если это не текст.</summary>
	private static string? Template(string text, int index)
	{
		var parts = new List<(string Template, bool Interpolated)>();
		var holes = 0;
		while (true)
		{
			index = SkipSpace(text, index);
			var interpolated = index < text.Length && text[index] == '$';
			var start = interpolated ? index + 1 : index;
			if (start >= text.Length || text[start] != '"' || text.AsSpan(start).StartsWith("\"\"\""))
			{
				return null;
			}

			var (template, end) = Literal(text, start + 1, interpolated, ref holes);
			parts.Add((template, interpolated));
			index = SkipSpace(text, end);
			if (index >= text.Length || text[index] != '+')
			{
				break;
			}

			index++;
		}

		// Сумма только интерполированных строк — одна интерполированная строка для обработчика;
		// сумма только литералов — константа. Смесь — обычная строка, шаблона у неё нет.
		if (parts.All(part => part.Interpolated))
		{
			return string.Concat(parts.Select(part => part.Template));
		}

		return parts.All(part => !part.Interpolated) && holes == 0
			? Phrase.Escape(string.Concat(parts.Select(part => Phrase.Render(part.Template, []))))
			: null;
	}

	private static (string Template, int End) Literal(string text, int index, bool interpolated, ref int holes)
	{
		var template = new StringBuilder();
		while (index < text.Length)
		{
			var c = text[index];
			if (c == '"')
			{
				return (template.ToString(), index + 1);
			}

			if (c == '\\')
			{
				template.Append(Phrase.Escape(Unescape(text, ref index)));
				continue;
			}

			if (interpolated && c == '{' && text[index + 1] == '{')
			{
				template.Append("{{");
				index += 2;
				continue;
			}

			if (interpolated && c == '}' && text[index + 1] == '}')
			{
				template.Append("}}");
				index += 2;
				continue;
			}

			if (interpolated && c == '{')
			{
				index = SkipHole(text, index + 1);
				template.Append('{').Append(holes++).Append('}');
				continue;
			}

			template.Append(interpolated ? c.ToString() : Phrase.Escape(c.ToString()));
			index++;
		}

		throw new InvalidOperationException("Строка не закрыта");
	}

	private static string Unescape(string text, ref int index)
	{
		var c = text[index + 1];
		index += 2;
		switch (c)
		{
			case 'n': return "\n";
			case 't': return "\t";
			case 'r': return "\r";
			case '0': return "\0";
			case 'u':
				var code = Convert.ToChar(Convert.ToInt32(text.Substring(index, 4), 16));
				index += 4;
				return code.ToString();
			default: return c.ToString();
		}
	}

	private static int SkipHole(string text, int index)
	{
		var depth = 1;
		var quoted = false;
		for (; index < text.Length; index++)
		{
			var c = text[index];
			if (quoted)
			{
				if (c == '\\')
				{
					index++;
				}
				else if (c == '"')
				{
					quoted = false;
				}
			}
			else if (c == '"')
			{
				quoted = true;
			}
			else if (c == '{')
			{
				depth++;
			}
			else if (c == '}' && --depth == 0)
			{
				return index + 1;
			}
		}

		throw new InvalidOperationException("Подстановка не закрыта");
	}

	private static int SkipSpace(string text, int index)
	{
		while (index < text.Length && char.IsWhiteSpace(text[index]))
		{
			index++;
		}

		return index;
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

	[GeneratedRegex(@"\bFailure\.(?:NotFound|Invalid|Forbidden|Unauthenticated|Conflict|Unavailable|TooLarge)\(|\bAgentReply\.Failure\(\s*""\w+""\s*,|\bPanelResults\.Refuse\(\s*StatusCodes\.\w+\s*,|\bPhrase\.(?:Format|From)\(")]
	private static partial Regex PhraseCall();

	[GeneratedRegex(@"\{\d+\}")]
	private static partial Regex Hole();

	[GeneratedRegex(@"\bResults\.Json\(|\bTypedResults\.Json\(|WriteAsJsonAsync\(")]
	private static partial Regex ResponseWriter();

	/// <summary>В файлы состояния фраза пишется объектом, поэтому <c>ToJsonString</c> запрещён только в маршрутах.</summary>
	[GeneratedRegex(@"ToJsonString\(\s*PanelJson\.Options\s*\)")]
	private static partial Regex StreamWriter();

	/// <summary>Сообщение отказа, присвоенное или возвращённое: <c>= x.Failure!.Message</c>, <c>=&gt; failure.Message</c>.</summary>
	[GeneratedRegex(@"(?:=>?|\?|:)\s*[\w.!]*[Ff]ailure!?\.Message\b")]
	private static partial Regex FailureMessage();

	[GeneratedRegex(@"[А-Яа-яЁё]")]
	private static partial Regex Cyrillic();
}
