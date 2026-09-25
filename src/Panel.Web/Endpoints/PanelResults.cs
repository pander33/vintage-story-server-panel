using Microsoft.AspNetCore.Http;
using Panel.Access.Authorization;
using Panel.Access.Domain;
using Panel.Contracts;
using Panel.Contracts.Http;
using Panel.SharedKernel;
using Panel.Web.Application;
using Panel.Web.Middleware;

namespace Panel.Web.Endpoints;

/// <summary>
/// Перевод результатов предметной области в HTTP, одно место для всех маршрутов:
/// соответствие «отказ → код» входит в замороженный контракт (ADR-16).
/// </summary>
public static class PanelResults
{
	public static IResult Json<T>(T value) => Results.Json(value, PanelJson.Options);

	/// <summary>Тело отказа как у FastAPI: <c>{"detail": "…"}</c>.</summary>
	public static IResult Refuse(int status, string detail) =>
		Results.Json(new ErrorResponse { Detail = detail }, PanelJson.Options, statusCode: status);

	public static IResult Refuse(Failure failure) => Refuse(StatusFor(failure.Code), failure.Message);

	public static IResult From<T>(Result<T> result) =>
		result.Match(Json, Refuse);

	public static int StatusFor(FailureCode code) => code switch
	{
		FailureCode.Unauthenticated => StatusCodes.Status401Unauthorized,
		FailureCode.Forbidden => StatusCodes.Status403Forbidden,
		FailureCode.NotFound => StatusCodes.Status404NotFound,
		FailureCode.Conflict => StatusCodes.Status409Conflict,
		FailureCode.Invalid => StatusCodes.Status400BadRequest,
		FailureCode.TooLarge => StatusCodes.Status413PayloadTooLarge,

		// Недоступная привилегированная служба или файл состояния — 503, а не 500: это внешнее
		// состояние, а не ошибка панели, и фронтенду нужно их различать.
		_ => StatusCodes.Status503ServiceUnavailable,
	};

	/// <summary>
	/// Требования к любому изменяющему маршруту: сессия, CSRF-токен, право команды. Порядок:
	/// сначала кто, потом тот ли это запрос, потом можно ли.
	/// </summary>
	public static Result<Identity> Authorize(
		HttpContext http,
		CommandAuthorizer authorizer,
		Type commandType,
		bool requireCsrf = true)
	{
		var identity = PanelContext.Get(http)?.Identity;
		if (identity is null)
		{
			return Failure.Unauthenticated("Требуется вход");
		}

		if (requireCsrf)
		{
			var csrf = CsrfGuard.Check(identity, http.Request.Headers[CsrfGuard.HeaderName]);
			if (!csrf.IsSuccess)
			{
				return Result.Fail<Identity>(csrf.Failure!);
			}
		}

		var authorized = authorizer.Authorize(identity, commandType);
		if (!authorized.IsSuccess || !requireCsrf || !commandType.IsDefined(typeof(MutatesServerAttribute), inherit: false))
		{
			return authorized;
		}

		// Последняя проверка: сервер, на котором идёт обслуживание, менять нельзя (ни службу, ни
		// настройки, ни содержимое).
		var instance = PanelContext.Get(http)?.Instance;
		var coordinator = http.RequestServices.GetService(typeof(MaintenanceCoordinator)) as MaintenanceCoordinator;
		return instance is { } server && coordinator is not null && coordinator.IsBusy(server)
			? Failure.Conflict("Дождитесь завершения обслуживания или обновления")
			: authorized;
	}

	public static string ClientAddress(HttpContext http) =>
		http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
