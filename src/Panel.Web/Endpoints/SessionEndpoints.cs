using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Panel.Access.Domain;
using Panel.Access.Ports;
using Panel.Access.Sessions;
using Panel.Contracts.Http;
using Panel.SharedKernel;
using Panel.Web.Application;
using Panel.Web.Middleware;

namespace Panel.Web.Endpoints;

/// <summary>Вход, выход и вопрос «кто я».</summary>
public static class SessionEndpoints
{
	public static void MapSession(this IEndpointRouteBuilder routes)
	{
		routes.MapGet("/api/session", (HttpContext http) =>
		{
			// Единственный маршрут, который отвечает 200 и без входа: так фронтенд узнаёт, что нужно
			// показать форму входа.
			var identity = PanelContext.Get(http)?.Identity;
			return PanelResults.Json(new SessionResponse
			{
				Authenticated = identity is not null,
				Csrf = identity?.CsrfToken,
				User = identity?.User,
				Role = identity?.Role.ToWire(),
			});
		});

		routes.MapPost("/api/login", async (
			HttpContext http,
			LoginRequest body,
			IAccountStore accounts,
			SessionWriter sessions,
			LoginThrottle throttle,
			AuditLog audit,
			PanelOptions options,
			TimeProvider time,
			CancellationToken ct) =>
		{
			var address = PanelResults.ClientAddress(http);

			var requested = body.Username.Trim();

			// Слишком длинное имя — 400: это ошибка запроса, а не неудачная попытка входа, и ограничитель
			// её не учитывает.
			if (requested.Length > UserName.MaxLength)
			{
				return PanelResults.Refuse(StatusCodes.Status400BadRequest, "Слишком длинное имя пользователя");
			}

			if (throttle.IsBlocked(address))
			{
				return PanelResults.Refuse(
					StatusCodes.Status429TooManyRequests, "Слишком много попыток. Подождите пять минут");
			}

			var directory = await accounts.LoadAsync(ct).ConfigureAwait(false);
			if (!directory.IsSuccess)
			{
				return PanelResults.Refuse(directory.Failure!);
			}

			// Имя недопустимой формы не может принадлежать учётной записи, поэтому это обычная неудачная
			// попытка.
			var name = UserName.Create(requested);
			var code = OneTimeCode.Create(body.Otp);
			var credentials = new Credentials(body.Password, code.IsSuccess ? code.Value : OneTimeCode.None);
			var user = name.IsSuccess
				? directory.Value.Authenticate(name.Value, credentials, time.GetUtcNow())
				: Result.Fail<AuthenticatedUser>(Failure.Unauthenticated("Неверные учётные данные"));

			if (!user.IsSuccess)
			{
				throttle.RecordFailure(address);
				await audit.RecordAsync(http, "login_failed", body.Username, ct).ConfigureAwait(false);

				// Текст отказа один для всех причин: неверный пароль, второй фактор или неизвестное имя.
				return PanelResults.Refuse(StatusCodes.Status401Unauthorized, "Неверный пароль");
			}

			throttle.Forget(address);
			var issued = sessions.Issue(user.Value, options.SessionLifetime);

			http.Response.Cookies.Append(PanelContextMiddleware.SessionCookie, issued.Token, new CookieOptions
			{
				HttpOnly = true,
				Secure = options.SecureCookie,
				SameSite = SameSiteMode.Strict,
				MaxAge = options.SessionLifetime,
				Path = "/",
			});

			await audit.RecordAsync(http, "login", user.Value.Name.Value, ct).ConfigureAwait(false);

			return PanelResults.Json(new LoginResponse
			{
				Csrf = issued.CsrfToken,
				User = user.Value.Name.Value,
				Role = user.Value.Role.ToWire(),
				Generation = user.Value.Generation,
			});
		});

		routes.MapPost("/api/logout", async (HttpContext http, AuditLog audit, CancellationToken ct) =>
		{
			// Выход требует CSRF-токена, но не права: он ничего не открывает.
			var identity = PanelContext.Get(http)?.Identity;
			if (identity is null)
			{
				return PanelResults.Refuse(StatusCodes.Status401Unauthorized, "Требуется вход");
			}

			var csrf = Access.Authorization.CsrfGuard.Check(
				identity, http.Request.Headers[Access.Authorization.CsrfGuard.HeaderName]);
			if (!csrf.IsSuccess)
			{
				return PanelResults.Refuse(csrf.Failure!);
			}

			http.Response.Cookies.Delete(PanelContextMiddleware.SessionCookie, new CookieOptions { Path = "/" });
			await audit.RecordAsync(http, "logout", identity.User, ct)
				.ConfigureAwait(false);

			return PanelResults.Json(OkResponse.Instance);
		});
	}
}
