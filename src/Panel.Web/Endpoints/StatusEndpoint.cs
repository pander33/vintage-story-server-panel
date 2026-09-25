using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Panel.Access.Authorization;
using Panel.Web.Application;
using Panel.Web.Middleware;

namespace Panel.Web.Endpoints;

public static class StatusEndpoint
{
	public static void MapStatus(this IEndpointRouteBuilder routes)
	{
		routes.MapGet("/api/status", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			GetServerStatusHandler handler,
			CancellationToken ct) =>
		{
			// Это чтение, поэтому CSRF-токен не нужен: он защищает от запроса с чужой страницы.
			var allowed = PanelResults.Authorize(http, authorizer, typeof(GetServerStatus), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var instance = PanelContext.Get(http)!.Instance;
			return PanelResults.From(await handler.HandleAsync(new GetServerStatus(instance), ct)
				.ConfigureAwait(false));
		});
	}
}
