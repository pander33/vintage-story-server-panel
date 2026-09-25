using Microsoft.AspNetCore.Http;
using Panel.Access.Sessions;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.SharedKernel;

namespace Panel.Web.Middleware;

/// <summary>
/// Разбирает cookie сессии и выбирает сервер, к которому относится запрос.
/// </summary>
/// <remarks>
/// Это сделано в одном месте, а не в каждом маршруте, по двум причинам:
/// 1. Заголовок X-Instance нужен клиентам API, а cookie vs_instance — EventSource, который не
///    умеет отправлять заголовки. Без cookie живой поток показывал бы другой сервер, чем
///    страница.
/// 2. Неизвестное имя сервера не ошибка (вкладка могла устареть): оно означает сервер по
///    умолчанию.
/// Отсутствие сессии здесь тоже не ошибка: 401 отвечает маршрут, потому что части маршрутов
/// (вход, мастер установки, статика) сессия не нужна.
/// </remarks>
public sealed class PanelContextMiddleware
{
	public const string SessionCookie = "vs_session";
	public const string InstanceCookie = "vs_instance";
	public const string InstanceHeader = "X-Instance";

	private readonly RequestDelegate next;

	public PanelContextMiddleware(RequestDelegate next) => this.next = next;

	public async Task InvokeAsync(HttpContext http, SessionGuard sessions, PanelOptions options, IRegistryStore registry)
	{
		var identity = await sessions
			.AuthenticateAsync(http.Request.Cookies[SessionCookie], http.RequestAborted)
			.ConfigureAwait(false);

		var requested = http.Request.Headers[InstanceHeader].ToString();
		if (string.IsNullOrWhiteSpace(requested))
		{
			requested = http.Request.Cookies[InstanceCookie] ?? string.Empty;
		}

		// Незнакомое имя не ошибка: реестр мог измениться, пока вкладка была открыта. Оно означает
		// сервер по умолчанию из реестра, а не PANEL_INSTANCE.
		var candidate = InstanceId.Create(requested);
		var snapshot = await registry.LoadAsync(http.RequestAborted).ConfigureAwait(false);
		var instance = RegistryRules.Resolve(
			snapshot,
			candidate.IsSuccess && !candidate.Value.IsRegistry ? candidate.Value : null,
			InstanceId.Parse(options.DefaultInstance));

		PanelContext.Set(http, new PanelContext(identity.IsSuccess ? identity.Value : null, instance));
		await next(http).ConfigureAwait(false);
	}
}
