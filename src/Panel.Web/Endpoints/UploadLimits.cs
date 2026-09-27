using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Panel.Web.Endpoints;

/// <summary>
/// Предел тела запроса для маршрутов загрузки.
/// </summary>
/// <remarks>
/// По умолчанию Kestrel ограничивает тело запроса 30 МБ, а разбор формы — 128 МБ на часть.
/// Без этого мод больше 30 МБ, мир и пакет сервера отвергались бы до маршрута с 413 от
/// сервера вместо понятного отказа панели. Здесь задаётся предел маршрута с запасом на
/// заголовки формы; точную проверку выполняет <c>StagingWriter</c>, считая байты при записи.
/// </remarks>
public static class UploadLimits
{
	private const long formOverhead = 1024 * 1024;

	/// <summary>Поднять предел тела запроса для маршрута с сырым телом; точный счёт байт ведёт запись.</summary>
	public static void RaiseBodyLimit(HttpContext http, long limitBytes)
	{
		ArgumentNullException.ThrowIfNull(http);
		if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body)
		{
			body.MaxRequestBodySize = limitBytes;
		}
	}

	/// <summary>Форма запроса; <c>null</c>, если запрос не форма. При превышении предела <c>TooLarge</c>.</summary>
	public static async Task<FormRead> ReadFormAsync(HttpContext http, long limitMegabytes, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(http);

		if (!http.Request.HasFormContentType)
		{
			return new FormRead(null, TooLarge: false);
		}

		var limit = limitMegabytes * 1024 * 1024 + formOverhead;
		if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } body)
		{
			body.MaxRequestBodySize = limit;
		}

		http.Features.Set<IFormFeature>(new FormFeature(http.Request, new FormOptions
		{
			MultipartBodyLengthLimit = limit,
		}));

		try
		{
			return new FormRead(await http.Request.ReadFormAsync(ct).ConfigureAwait(false), TooLarge: false);
		}
		catch (Exception exception) when (exception is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge }
													 or InvalidDataException)
		{
			return new FormRead(null, TooLarge: true);
		}
	}
}

/// <summary>Результат чтения формы с пределом.</summary>
public sealed record FormRead(IFormCollection? Form, bool TooLarge);
