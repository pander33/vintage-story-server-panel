using Microsoft.AspNetCore.Http;
using Panel.Access.Domain;
using Panel.Access.Sessions;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.SharedKernel;
using Panel.Web.Application;
using Panel.Web.Endpoints;

namespace Panel.Web.Middleware;

/// <summary>
/// Узнаёт, кто выполняет запрос (API-токен из <c>Authorization: Bearer</c> или cookie сессии), и
/// выбирает сервер, к которому запрос относится.
/// </summary>
/// <remarks>
/// Это сделано в одном месте, а не в каждом маршруте, по двум причинам:
/// 1. Заголовок X-Instance нужен клиентам API, а cookie vs_instance — EventSource, который не
///    умеет отправлять заголовки. Без cookie живой поток показывал бы другой сервер, чем
///    страница.
/// 2. Неизвестное имя сервера не ошибка (вкладка могла устареть): оно означает сервер по
///    умолчанию.
/// Отсутствие сессии здесь тоже не ошибка: 401 отвечает маршрут, потому что части маршрутов
/// (вход, мастер установки, статика) сессия не нужна. Исключение — токен, который нельзя
/// проверить (файл токенов или учётных записей не читается): это 503 сразу, а не «недействительный
/// токен», иначе Prometheus и скрипты видели бы отзыв там, где сломано хранилище.
/// </remarks>
public sealed class PanelContextMiddleware
{
	public const string SessionCookie = "vs_session";
	public const string InstanceCookie = "vs_instance";
	public const string InstanceHeader = "X-Instance";

	private readonly RequestDelegate next;

	public PanelContextMiddleware(RequestDelegate next) => this.next = next;

	public async Task InvokeAsync(
		HttpContext http,
		SessionGuard sessions,
		ApiTokenGuard tokens,
		PanelInstance panelInstance,
		IRegistryStore registry)
	{
		var bearer = BearerToken(http);
		var identity = bearer is not null
			? await tokens.AuthenticateAsync(bearer, http.RequestAborted).ConfigureAwait(false)
			: await sessions
				.AuthenticateAsync(http.Request.Cookies[SessionCookie], http.RequestAborted)
				.ConfigureAwait(false);

		if (bearer is not null && identity.Failure is { Code: FailureCode.Unavailable } broken)
		{
			await PanelResults.Refuse(broken).ExecuteAsync(http).ConfigureAwait(false);
			return;
		}

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
			panelInstance.Id);

		PanelContext.Set(http, new PanelContext(identity.IsSuccess ? identity.Value : null, instance));
		await next(http).ConfigureAwait(false);
	}

	/// <summary>
	/// Токен из <c>Authorization: Bearer</c>. Если заголовок есть, cookie сессии не читается вовсе:
	/// запрос с неверным токеном не должен пройти по случайно приложенной сессии.
	/// </summary>
	private static string? BearerToken(HttpContext http)
	{
		var header = http.Request.Headers.Authorization.ToString();
		const string scheme = "Bearer ";
		return header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? header[scheme.Length..].Trim() : null;
	}
}
