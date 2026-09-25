using System.Text;
using Microsoft.AspNetCore.Http;

namespace Panel.Web.Endpoints;

/// <summary>
/// Поток <c>text/event-stream</c>: <c>data: …</c> для нового и <c>: keep-alive</c>, когда
/// нового нет.
/// </summary>
/// <remarks>
/// Строка поддержания связи нужна потому, что обратный прокси закрывает молчащее соединение,
/// EventSource переподключается с задержкой, и график на это время замирает.
/// <c>X-Accel-Buffering: no</c> запрещает nginx копить события.
/// </remarks>
public static class ServerSentEvents
{
	public const string KeepAlive = ": keep-alive\n\n";

	/// <summary>Заголовки потока. Вызывается до первого кадра.</summary>
	public static void Begin(HttpContext http)
	{
		ArgumentNullException.ThrowIfNull(http);

		http.Response.ContentType = "text/event-stream";
		http.Response.Headers.CacheControl = "no-cache";
		http.Response.Headers["X-Accel-Buffering"] = "no";
	}

	/// <summary>Кадр целиком с немедленной отправкой клиенту.</summary>
	public static async Task SendAsync(HttpContext http, string frame, CancellationToken ct)
	{
		await http.Response.WriteAsync(frame, Encoding.UTF8, ct).ConfigureAwait(false);
		await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
	}

	public static string Data(string json) => $"data: {json}\n\n";

	/// <summary>
	/// Поток «снимок, если изменился» для телеметрии. <paramref name="next"/> получает предыдущий
	/// отправленный снимок и возвращает новый или <c>null</c>.
	/// </summary>
	public static async Task StreamAsync(
		HttpContext http,
		TimeSpan interval,
		CancellationToken ct,
		Func<string?, Task<string?>> next)
	{
		ArgumentNullException.ThrowIfNull(next);

		Begin(http);

		string? previous = null;
		try
		{
			while (!ct.IsCancellationRequested)
			{
				var payload = await next(previous).ConfigureAwait(false);
				if (payload is not null)
				{
					previous = payload;
				}

				await SendAsync(http, payload is null ? KeepAlive : Data(payload), ct).ConfigureAwait(false);
				await Task.Delay(interval, ct).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			// Клиент отключился: обычное завершение потока.
		}
	}
}
