using Microsoft.AspNetCore.Http;

namespace Panel.Web.Middleware;

/// <summary>
/// Заголовки безопасности на каждом ответе.
/// </summary>
/// <remarks>
/// Фронтенд рассчитан на этот CSP: встроенных скриптов и стилей нет, всё загружается со своего
/// адреса. <c>no-store</c> стоит и на статике, поэтому <c>?v=</c> в адресах нужен только на
/// случай прокси. Заголовки ставятся перед отправкой ответа и перекрывают заголовки маршрута.
/// </remarks>
public sealed class SecurityHeadersMiddleware
{
	public static readonly IReadOnlyList<KeyValuePair<string, string>> Headers =
	[
		new("X-Content-Type-Options", "nosniff"),
		new("X-Frame-Options", "DENY"),
		new("Referrer-Policy", "no-referrer"),
		new("Content-Security-Policy", "default-src 'self'; style-src 'self'; script-src 'self'; connect-src 'self'"),
		new("Cache-Control", "no-store"),
	];

	private readonly RequestDelegate next;

	public SecurityHeadersMiddleware(RequestDelegate next) => this.next = next;

	public Task InvokeAsync(HttpContext http)
	{
		http.Response.OnStarting(() =>
		{
			Apply(http.Response.Headers);
			return Task.CompletedTask;
		});
		return next(http);
	}

	public static void Apply(IHeaderDictionary headers)
	{
		foreach (var (name, value) in Headers)
		{
			headers[name] = value;
		}
	}
}
