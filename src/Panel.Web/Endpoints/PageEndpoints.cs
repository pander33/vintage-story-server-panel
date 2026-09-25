using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.FileProviders;
using Panel.Web.Application;

namespace Panel.Web.Endpoints;

/// <summary>
/// Две страницы и статика.
/// </summary>
/// <remarks>
/// <para>
/// Пока мастер не завершён, главная страница перенаправляет на <c>/setup</c>, а после
/// завершения <c>/setup</c> перенаправляет на главную. Сессия для страниц не нужна: кто вошёл
/// и что показывать, фронтенд узнаёт из <c>/api/session</c> и <c>/api/setup/state</c>.
/// Перенаправление 307.
/// </para>
/// <para>
/// Фронтенд встроен в сборку (ADR-17). Под <c>/static</c> отдаётся весь каталог, включая обе
/// страницы.
/// </para>
/// </remarks>
public static class PageEndpoints
{
	public const string IndexPage = "index.html";
	public const string SetupPage = "setup.html";

	/// <summary>Встроенный фронтенд; имя ресурса задано в <c>Panel.Web.csproj</c>.</summary>
	public static IFileProvider Assets { get; } = new EmbeddedFileProvider(typeof(PageEndpoints).Assembly, "Panel.Web.Static");

	public static IApplicationBuilder UseFrontendAssets(this IApplicationBuilder app) =>
		app.UseStaticFiles(new StaticFileOptions { RequestPath = "/static", FileProvider = Assets });

	public static void MapPages(this IEndpointRouteBuilder routes)
	{
		routes.MapGet("/favicon.ico", () => TypedResults.Stream(Assets.GetFileInfo("favicon.ico").CreateReadStream(), "image/x-icon"));
		routes.MapGet("/", (SetupService setup, CancellationToken ct) => IndexAsync(setup, ct));
		routes.MapGet("/setup", (SetupService setup, CancellationToken ct) => SetupAsync(setup, ct));
	}

	public static async Task<IResult> IndexAsync(SetupService setup, CancellationToken ct) =>
		(await setup.CurrentAsync(ct).ConfigureAwait(false)).IsCompleted
			? Page(IndexPage)
			: TypedResults.Redirect("/setup", permanent: false, preserveMethod: true);

	/// <summary>Страница мастера, которая показывается вместо вкладок, пока на них нечего показать.</summary>
	public static async Task<IResult> SetupAsync(SetupService setup, CancellationToken ct) =>
		(await setup.CurrentAsync(ct).ConfigureAwait(false)).IsCompleted
			? TypedResults.Redirect("/", permanent: false, preserveMethod: true)
			: Page(SetupPage);

	private static FileStreamHttpResult Page(string name) =>
		TypedResults.Stream(Assets.GetFileInfo(name).CreateReadStream(), "text/html; charset=utf-8");
}
