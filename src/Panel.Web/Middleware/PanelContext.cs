using Microsoft.AspNetCore.Http;
using Panel.Access.Domain;
using Panel.SharedKernel;

namespace Panel.Web.Middleware;

/// <summary>
/// Кто выполняет запрос и к какому серверу он относится; определяется один раз на запрос.
/// </summary>
/// <remarks>
/// Сервер запроса передаётся явно, а не через AsyncLocal: фоновый цикл, запущенный не там, где
/// ожидалось, унаследовал бы чужой контекст и обслуживал бы не тот сервер.
/// </remarks>
public sealed record PanelContext(Identity? Identity, InstanceId Instance)
{
	private const string key = "panel.context";

	public static void Set(HttpContext http, PanelContext context) => http.Items[key] = context;

	public static PanelContext? Get(HttpContext http) => http.Items.TryGetValue(key, out var value)
		? value as PanelContext
		: null;
}
