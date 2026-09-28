using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Panel.Access.Domain;
using Panel.Access.Infrastructure;
using Panel.Access.Passwords;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.SharedKernel;
using Panel.Web.Application;
using Panel.Web.Endpoints;
using Panel.Web.Middleware;

namespace Panel.Web.Tests;

/// <summary>
/// Главная страница и мастер: перенаправления, содержимое сборки и заголовки ответов.
/// </summary>
public sealed partial class PageEndpointsTests : IDisposable
{
	private readonly string state = Path.Combine(Path.GetTempPath(), "vs-pages-" + Guid.NewGuid().ToString("n"));
	private readonly PanelOptions options;

	public PageEndpointsTests()
	{
		Directory.CreateDirectory(state);
		options = new PanelOptions { StateDirectory = state, DefaultInstance = "main" };
	}

	public void Dispose()
	{
		if (Directory.Exists(state))
		{
			Directory.Delete(state, recursive: true);
		}
	}

	private async Task<SetupService> SetupAsync(bool completed)
	{
		var accounts = new FileAccountStore(options.AccountsPath, options.LegacyPasswordPath);
		if (completed)
		{
			var directory = new AccountDirectory([]).Put(UserName.Administrator, PasswordHash.Create("correct horse battery").Value, Role.Admin).Value;
			Assert.True((await accounts.SaveAsync(directory, CancellationToken.None)).IsSuccess);
		}

		var setup = new SetupService(options, accounts, new MainOnly(), TimeProvider.System, NullLogger<SetupService>.Instance);
		await setup.StartAsync(CancellationToken.None);
		Assert.Equal(completed, (await setup.CurrentAsync(CancellationToken.None)).IsCompleted);
		return setup;
	}

	private static string Text(string name)
	{
		using var reader = new StreamReader(PageEndpoints.Assets.GetFileInfo(name).CreateReadStream());
		return reader.ReadToEnd();
	}

	[Fact]
	public async Task Пока_мастер_открыт_главная_уводит_на_него()
	{
		var setup = await SetupAsync(completed: false);

		var index = Assert.IsType<RedirectHttpResult>(await PageEndpoints.IndexAsync(setup, CancellationToken.None));
		var page = Assert.IsType<FileStreamHttpResult>(await PageEndpoints.SetupAsync(setup, CancellationToken.None));

		Assert.Equal("/setup", index.Url);
		Assert.True(index.PreserveMethod);
		Assert.False(index.Permanent);
		Assert.Equal("text/html; charset=utf-8", page.ContentType);
	}

	[Fact]
	public async Task Завершённый_мастер_уводит_на_главную()
	{
		var setup = await SetupAsync(completed: true);

		var page = Assert.IsType<FileStreamHttpResult>(await PageEndpoints.IndexAsync(setup, CancellationToken.None));
		var wizard = Assert.IsType<RedirectHttpResult>(await PageEndpoints.SetupAsync(setup, CancellationToken.None));

		Assert.Equal("/", wizard.Url);
		Assert.True(wizard.PreserveMethod);
		using var reader = new StreamReader(page.FileStream);
		Assert.Contains("/static/core.js", await reader.ReadToEndAsync());
	}

	[Fact]
	public void Фронтенд_встроен_в_сборку_целиком()
	{
		string[] expected =
		[
			"apple-touch-icon.png", "backups.js", "catalog.js", "console-commands.js", "content.js", "core.js", "dependencies.js", "en.js", "favicon-32.png", "favicon.ico", "favicon.svg",
			"i18n.js", "index.html", "logs.js", "maintenance.js", "modpack.js", "overview.js", "panel.css", "players.js", "ru.js", "settings.js", "setup.html", "setup.js",
			"style.css", "world-schema.js",
		];

		Assert.Equal(expected, PageEndpoints.Assets.GetDirectoryContents("").Select(file => file.Name).Order(StringComparer.Ordinal));
	}

	/// <summary>
	/// Каталоги — скрипты с defer перед i18n.js: синхронный запрос каталога держал основной поток
	/// браузера, и медленный ответ панели вешал вкладку.
	/// </summary>
	[Theory]
	[InlineData(PageEndpoints.IndexPage, "core.js")]
	[InlineData(PageEndpoints.SetupPage, "setup.js")]
	public void Каталоги_загружаются_скриптами_до_локализации(string name, string first)
	{
		var page = Text(name);
		string[] order = ["en.js", "ru.js", "i18n.js", first];
		var positions = order.Select(file => page.IndexOf($"/static/{file}?v=0.2.0\" defer", StringComparison.Ordinal)).ToList();

		Assert.All(positions, position => Assert.True(position >= 0));
		Assert.Equal(positions.Order(), positions);
		Assert.DoesNotContain("XMLHttpRequest", Text("i18n.js"), StringComparison.Ordinal);
	}

	[Fact]
	public void Ключи_интерфейса_есть_в_обоих_каталогах()
	{
		using var english = Catalog("en.js");
		using var russian = Catalog("ru.js");
		var englishKeys = Keys(english.RootElement).ToHashSet(StringComparer.Ordinal);
		var russianKeys = Keys(russian.RootElement).ToHashSet(StringComparer.Ordinal);

		Assert.Equal(englishKeys.Order(StringComparer.Ordinal), russianKeys.Order(StringComparer.Ordinal));
		foreach (var file in PageEndpoints.Assets.GetDirectoryContents("").Where(file => file.Name.EndsWith(".js", StringComparison.Ordinal) || file.Name.EndsWith(".html", StringComparison.Ordinal)))
		{
			var source = Text(file.Name);
			foreach (Match match in TranslationKey().Matches(source))
			{
				Assert.Contains(match.Groups["key"].Value, englishKeys);
			}
		}
	}

	/// <summary>
	/// Каталог — литерал объекта JS в одинарных кавычках (<c>.editorconfig</c>); для разбора
	/// строки переводятся в двойные кавычки JSON.
	/// </summary>
	private static JsonDocument Catalog(string name)
	{
		var source = Text(name);
		var start = source.IndexOf("= {", source.IndexOf("??= {})", StringComparison.Ordinal) + 7, StringComparison.Ordinal) + 2;
		var end = source.LastIndexOf("};", StringComparison.Ordinal) + 1;
		var json = new System.Text.StringBuilder(end - start);
		var quoted = false;
		for (var index = start; index < end; index++)
		{
			var character = source[index];
			if (!quoted)
			{
				quoted = character == '\'';
				json.Append(quoted ? '"' : character);
			}
			else if (character == '\\' && source[index + 1] == '\'')
			{
				json.Append('\'');
				index++;
			}
			else if (character == '\\')
			{
				json.Append(character).Append(source[++index]);
			}
			else if (character == '\'')
			{
				quoted = false;
				json.Append('"');
			}
			else
			{
				json.Append(character == '"' ? "\\\"" : character.ToString());
			}
		}

		return JsonDocument.Parse(json.ToString(), new JsonDocumentOptions { AllowTrailingCommas = true });
	}

	private static IEnumerable<string> Keys(JsonElement node, string prefix = "")
	{
		foreach (var item in node.EnumerateObject())
		{
			var key = prefix + item.Name;
			if (item.Value.ValueKind == JsonValueKind.Object)
			{
				foreach (var child in Keys(item.Value, key + ".")) yield return child;
			}
			else yield return key;
		}
	}

	[Theory]
	[InlineData(PageEndpoints.IndexPage)]
	[InlineData(PageEndpoints.SetupPage)]
	public void Страница_ссылается_только_на_встроенные_файлы(string name)
	{
		var references = StaticReference().Matches(Text(name)).Select(match => match.Groups["file"].Value).ToList();

		Assert.NotEmpty(references);
		Assert.All(references, file => Assert.True(PageEndpoints.Assets.GetFileInfo(file).Exists, file));
	}

	/// <summary>CSP <c>script-src 'self'</c> без предупреждения заблокирует любой встроенный скрипт.</summary>
	[Theory]
	[InlineData(PageEndpoints.IndexPage)]
	[InlineData(PageEndpoints.SetupPage)]
	public void На_странице_нет_встроенных_скриптов(string name)
	{
		var scripts = ScriptTag().Matches(Text(name)).Select(match => match.Value).ToList();

		Assert.NotEmpty(scripts);
		Assert.All(scripts, tag => Assert.Contains(" src=", tag));
	}

	[Fact]
	public async Task Заголовки_безопасности_перекрывают_поставленные_маршрутом()
	{
		var response = new RecordingResponse();
		var http = new DefaultHttpContext();
		http.Features.Set<IHttpResponseFeature>(response);
		var middleware = new SecurityHeadersMiddleware(context =>
		{
			context.Response.Headers.CacheControl = "no-cache";
			return Task.CompletedTask;
		});

		await middleware.InvokeAsync(http);
		await response.StartAsync();

		Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
		Assert.Equal("DENY", http.Response.Headers["X-Frame-Options"].ToString());
		Assert.Equal("nosniff", http.Response.Headers.XContentTypeOptions.ToString());
		Assert.Equal("no-referrer", http.Response.Headers["Referrer-Policy"].ToString());
		Assert.Equal(
			"default-src 'self'; style-src 'self'; script-src 'self'; connect-src 'self'",
			http.Response.Headers.ContentSecurityPolicy.ToString());
	}

	[GeneratedRegex(@"/static/(?<file>[A-Za-z0-9._-]+)\?v=")]
	private static partial Regex StaticReference();

	[GeneratedRegex(@"<script\b[^>]*>")]
	private static partial Regex ScriptTag();

	[GeneratedRegex(@"(?:I18N\.t\(|data-i18n(?:-placeholder|-title|-aria-label|-value)?=)['""'](?<key>[A-Za-z0-9.]+)['""']")]
	private static partial Regex TranslationKey();

	private sealed class RecordingResponse : HttpResponseFeature
	{
		private readonly List<(Func<object, Task> Callback, object State)> starting = [];

		public override void OnStarting(Func<object, Task> callback, object state) => starting.Add((callback, state));

		public async Task StartAsync()
		{
			foreach (var (callback, state) in starting)
			{
				await callback(state);
			}
		}
	}

	private sealed class MainOnly : IRegistryStore
	{
		public Task<RegistrySnapshot> LoadAsync(CancellationToken ct) =>
			Task.FromResult<RegistrySnapshot>(new RegistrySnapshot.Available(
				[new RegisteredServer { Id = InstanceId.Parse("main"), Name = "main", Unit = "vs-main.service" }],
				null,
				RegistryStamp.Absent));
	}
}
