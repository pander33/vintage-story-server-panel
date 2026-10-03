using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Panel.Access.Authorization;
using Panel.Access.Domain;
using Panel.Access.Infrastructure;
using Panel.Access.Passwords;
using Panel.Access.Sessions;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.SharedKernel;
using Panel.Web.Application;
using Panel.Web.Endpoints;
using Panel.Web.Middleware;

namespace Panel.Web.Tests;

/// <summary>
/// Запрос с <c>Authorization: Bearer</c>: удостоверение по токену, без CSRF, без запасного пути
/// через cookie; неверные токены с того же адреса верный не запирают (findings §82).
/// </summary>
public sealed class ApiTokenRequestTests : IDisposable
{
	private static readonly PasswordHash password = PasswordHash.Create("пароль достаточной длины").Value;

	private readonly string directory = Path.Combine(Path.GetTempPath(), "vs-bearer-" + Guid.NewGuid().ToString("n"));
	private readonly FileAccountStore accounts;
	private readonly FileApiTokenStore tokens;
	private readonly SessionSecret secret = SessionSecret.Create([.. Enumerable.Range(0, 48).Select(value => (byte) value)]).Value;

	public ApiTokenRequestTests()
	{
		Directory.CreateDirectory(directory);
		accounts = new FileAccountStore(Path.Combine(directory, "accounts.json"), string.Empty);
		tokens = new FileApiTokenStore(Path.Combine(directory, "api-tokens.json"));
	}

	public void Dispose() => Directory.Delete(directory, recursive: true);

	[Fact]
	public async Task Токен_даёт_удостоверение_и_изменение_без_CSRF()
	{
		var plain = await IssueAsync(Role.Operator);
		var http = Request($"Bearer {plain}");

		await RunAsync(http);

		var identity = PanelContext.Get(http)!.Identity!;
		Assert.True(identity.ViaApiToken);
		Assert.Equal(Role.Operator, identity.Role);

		// CSRF-заголовка нет, а изменяющая проверка проходит: токен прислать чужой сайт не может.
		Assert.True(PanelResults.Authorize(http, new CommandAuthorizer(), typeof(ExportMetrics), requireCsrf: true).IsSuccess);
		Assert.Equal(FailureCode.Forbidden, PanelResults.Authorize(http, new CommandAuthorizer(), typeof(IssueApiToken)).Failure!.Code);
	}

	/// <summary>
	/// Даже токен администратора не делает того, чем утёкший токен расширил бы доступ: произвольная
	/// консоль, вебхуки, адрес панели, каталог, создание и удаление серверов.
	/// </summary>
	[Theory]
	[InlineData(typeof(SendRawConsole))]
	[InlineData(typeof(ConfigureNotifications))]
	[InlineData(typeof(RunSetupWizard))]
	[InlineData(typeof(ChangeServersRoot))]
	[InlineData(typeof(ManageProvision))]
	[InlineData(typeof(ManageInstances))]
	public async Task Токен_администратора_не_выполняет_команды_сессии(Type command)
	{
		var http = Request($"Bearer {await IssueAsync(Role.Admin)}");

		await RunAsync(http);

		Assert.Equal(Role.Admin, PanelContext.Get(http)!.Identity!.Role);
		Assert.Equal(FailureCode.Forbidden, PanelResults.Authorize(http, new CommandAuthorizer(), command).Failure!.Code);
	}

	/// <summary>Неверный токен не заменяется сессией из cookie того же запроса.</summary>
	[Fact]
	public async Task С_неверным_токеном_cookie_не_читается()
	{
		await IssueAsync(Role.Viewer);
		var session = new SessionWriter(secret).Issue(new AuthenticatedUser(UserName.Parse("admin"), Role.Admin, 1), TimeSpan.FromHours(1));
		var http = Request("Bearer vsp_0123456789abcdef_wrong");
		http.Request.Headers.Cookie = $"{PanelContextMiddleware.SessionCookie}={session.Token}";

		await RunAsync(http);

		Assert.Null(PanelContext.Get(http)!.Identity);
	}

	/// <summary>Чужие неверные токены за общим NAT не запирают верный токен с того же адреса.</summary>
	[Fact]
	public async Task Неверные_токены_с_адреса_не_запирают_верный()
	{
		var plain = await IssueAsync(Role.Viewer);
		for (var attempt = 0; attempt < LoginThrottle.MaxAttempts * 2; attempt++)
		{
			await RunAsync(Request("Bearer vsp_0123456789abcdef_wrong"));
		}

		var valid = Request($"Bearer {plain}");
		await RunAsync(valid);
		Assert.True(PanelContext.Get(valid)!.Identity!.ViaApiToken);
	}

	/// <summary>
	/// Испорченный файл токенов — 503 сразу, а не «недействительный токен»: иначе сломанное
	/// хранилище выглядело бы для Prometheus как отзыв.
	/// </summary>
	[Fact]
	public async Task Непрочитанный_файл_токенов_даёт_503()
	{
		var plain = await IssueAsync(Role.Viewer);
		await File.WriteAllTextAsync(Path.Combine(directory, "api-tokens.json"), "{ испорчен");
		var http = Request($"Bearer {plain}");
		http.Response.Body = new MemoryStream();
		http.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
		var reached = false;

		await new PanelContextMiddleware(_ =>
		{
			reached = true;
			return Task.CompletedTask;
		}).InvokeAsync(
			http,
			new SessionGuard(new SessionReader(secret), accounts),
			new ApiTokenGuard(tokens, accounts, TimeProvider.System),
			new PanelInstance(new PanelOptions { StateDirectory = directory }),
			new EmptyRegistry());

		Assert.False(reached);
		Assert.Equal(StatusCodes.Status503ServiceUnavailable, http.Response.StatusCode);
	}

	private async Task<string> IssueAsync(Role role)
	{
		var directory = new AccountDirectory([
			new Account { Name = UserName.Parse("admin"), Password = password, Role = Role.Admin, Generation = 1 },
		]);
		await accounts.SaveAsync(directory, CancellationToken.None);
		var owner = directory.Authenticate(UserName.Parse("admin"), new Credentials("пароль достаточной длины"), DateTimeOffset.UtcNow).Value;
		return (await tokens.ChangeAsync(
			set => set.Issue(owner, new ApiTokenDraft(ApiTokenName.Create("ci").Value, role, null), DateTimeOffset.UtcNow)
				.Map(issued => (issued.Tokens, issued.Plain)),
			CancellationToken.None)).Value;
	}

	private Task RunAsync(HttpContext http) =>
		new PanelContextMiddleware(_ => Task.CompletedTask).InvokeAsync(
			http,
			new SessionGuard(new SessionReader(secret), accounts),
			new ApiTokenGuard(tokens, accounts, TimeProvider.System),
			new PanelInstance(new PanelOptions { StateDirectory = directory }),
			new EmptyRegistry());

	private static DefaultHttpContext Request(string authorization)
	{
		var http = new DefaultHttpContext();
		http.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
		http.Request.Headers.Authorization = authorization;
		return http;
	}

	private sealed class EmptyRegistry : IRegistryStore
	{
		public Task<RegistrySnapshot> LoadAsync(CancellationToken ct) =>
			Task.FromResult<RegistrySnapshot>(new RegistrySnapshot.Available([], null, RegistryStamp.Absent));
	}
}
