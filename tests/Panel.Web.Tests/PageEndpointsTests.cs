using System.Text.RegularExpressions;
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
			"apple-touch-icon.png", "backups.js", "content.js", "core.js", "favicon-32.png", "favicon.ico", "favicon.svg",
			"index.html", "logs.js", "maintenance.js", "overview.js", "panel.css", "players.js", "settings.js", "setup.html", "setup.js",
			"style.css",
		];

		Assert.Equal(expected, PageEndpoints.Assets.GetDirectoryContents("").Select(file => file.Name).Order(StringComparer.Ordinal));
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
