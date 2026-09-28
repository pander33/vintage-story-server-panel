using System.Runtime.CompilerServices;
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
	/// <summary>
	/// Ответ JSON на языке запроса: фразы (<see cref="Phrase"/>) в нём пишутся переведёнными строками
	/// (<see cref="LocalizedJson"/>).
	/// </summary>
	public static IResult Json<T>(T value, int? status = null) => new Localized<T>(value, status);

	/// <summary>Тело отказа <c>{"detail": "…"}</c>; текст переводится при записи, как в <see cref="Json{T}"/>.</summary>
	public static IResult Refuse(int status, Phrase detail) => Json(new ErrorResponse { Detail = detail }, status);

	public static IResult Refuse(int status, string detail) => Refuse(status, Phrase.From(detail));

	[OverloadResolutionPriority(1)]
	public static IResult Refuse(int status, ref PhraseHandler detail) => Refuse(status, detail.ToPhrase());

	public static IResult Refuse(Failure failure) => Refuse(StatusFor(failure.Code), failure.Text);

	public static IResult From<T>(Result<T> result) =>
		result.Match(value => Json(value), Refuse);

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

	private sealed class Localized<T>(T value, int? status) : IResult, IStatusCodeHttpResult
	{
		public int? StatusCode => status;

		public Task ExecuteAsync(HttpContext httpContext) =>
			Results.Json(value, LocalizedJson.For(httpContext), statusCode: status).ExecuteAsync(httpContext);
	}
}
