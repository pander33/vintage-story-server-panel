using Microsoft.AspNetCore.HttpOverrides;
using Panel.Access.Sessions;
using Panel.Web;
using Panel.Web.Endpoints;
using Panel.Web.Middleware;

var options = PanelOptions.FromEnvironment();

// Команды установщика: init, claim-token, set-password, rotate-secret, provision. Тот же граф
// служб, но без хоста.
if (args is ["manage", .. var command])
{
	return await ManageCommands.RunAsync(command, options, Console.Out, Console.Error);
}

var builder = WebApplication.CreateBuilder(args);

// Служба Windows vs-panel отвечает диспетчеру служб. Вне службы вызов ничего не делает.
builder.Host.UseWindowsService(service => service.ServiceName = "vs-panel");

// Без строки на каждый HTTP-запрос. Поставка — один файл без appsettings.json (publish.sh его
// удаляет), поэтому правило задано здесь: иначе journald получал бы каждый запрос статики вместе
// с адресом, а в нём и одноразовый токен мастера (/setup?token=…), и в журнале панели, который
// уходит в диагностику, не оставалось бы места для отказов.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

// На Windows журнал службы панели — журнал событий Windows, его читает через демон раздел
// «Журнал службы панели». По умолчанию туда пишутся только предупреждения; здесь пишется то
// же, что journald получает на Linux.
if (OperatingSystem.IsWindows())
{
	builder.Logging.AddFilter<Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider>(null, LogLevel.Information);
	builder.Logging.AddFilter<Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider>("Microsoft.AspNetCore", LogLevel.Warning);
}

// PANEL_BIND и PANEL_PORT из vs-panel.env. Без этого Kestrel слушал бы localhost:5000, и
// настройки шага «Доступ» ни на что бы не влияли.
builder.WebHost.ConfigureKestrel(kestrel =>
{
	if (options.Bind is "localhost")
	{
		kestrel.ListenLocalhost(options.Port);
	}
	else
	{
		kestrel.Listen(System.Net.IPAddress.Parse(options.Bind), options.Port);
	}
});

// PANEL_ALLOWED_HOSTS: чужой заголовок Host получает 400.
builder.Services.AddHostFiltering(filtering =>
{
	filtering.AllowedHosts = [.. options.AllowedHosts];
	filtering.AllowEmptyHosts = false;
});

// PANEL_TRUSTED_PROXIES: адрес посетителя за обратным прокси.
builder.Services.Configure<ForwardedHeadersOptions>(forwarded => ProxyTrust.Configure(forwarded, options.TrustedProxies));

// Ключ сессий обязателен: без него панель не отличит свою сессию от чужой, и лучше упасть
// при старте.
var secret = SessionSecret.Load(options.SecretKeyPath);
if (!secret.IsSuccess)
{
	Console.Error.WriteLine(secret.Failure!.Message);
	return 1;
}

builder.Services.AddPanel(options, secret.Value);

var app = builder.Build();

// Заголовки безопасности ставятся первыми: их получает любой ответ, включая отказ по Host и
// статику.
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseForwardedHeaders();
app.UseHostFiltering();

// Статика раньше контекста панели: файлам фронтенда не нужны ни сессия, ни реестр.
app.UseFrontendAssets();
app.UseMiddleware<PanelContextMiddleware>();
app.MapPages();
app.MapSession();
app.MapAccounts();
app.MapStatus();
app.MapRuntime();
app.MapMods();
app.MapCatalogue();
app.MapWorlds();
app.MapBackups();
app.MapConfiguration();
app.MapObservability();
app.MapTelemetry();
app.MapEvents();
app.MapHistory();
app.MapInstances();
app.MapAutomation();
app.MapUpdates();
app.MapProvision();
app.MapSetup();

await app.RunAsync();
return 0;
