using Panel.SharedKernel;

namespace Panel.Catalogues.Infrastructure;

/// <summary>Проверенная картинка: тип определён по первым байтам, а не по заголовку ответа.</summary>
public sealed record ModImage(string ContentType, byte[] Data);

/// <summary>
/// Прокси картинок ModDB. CSP панели разрешает только свой хост, а <c>no-store</c> не даёт браузеру
/// сохранять картинки, поэтому их отдаёт и кэширует сама панель.
/// </summary>
/// <remarks>
/// Только https и только хост CDN ModDB — та же проверка, что у архивов. Переадресации не
/// следует. Предел — 2 МБ на картинку и 10 с ожидания. Тип — png, jpeg, gif или webp, и по заголовку
/// ответа, и по первым байтам файла; SVG нет: это документ со скриптами. Кэш в памяти: общий предел
/// 16 МБ, при нехватке вытесняются давние. Обложка мода — десятки килобайт, так что в кэш входят
/// сотни, а панель живёт на одной машине с игрой, и память ей нужнее.
/// </remarks>
public sealed class ModImageProxy
{
	public const int MaxImageBytes = 2 * 1024 * 1024;

	public const long CacheBytes = 16L * 1024 * 1024;

	private const string userAgent = "VintageStoryPanel/0.2.0";

	private readonly HttpClient http;
	private readonly TimeProvider clock;
	private readonly Lock gate = new();
	private readonly Dictionary<string, (ModImage Image, long At)> cache = new(StringComparer.Ordinal);
	private long cached;

	/// <param name="http">Клиент без автоматических переадресаций: иначе проверка хоста бессмысленна.</param>
	public ModImageProxy(HttpClient http, TimeProvider clock)
	{
		ArgumentNullException.ThrowIfNull(http);
		ArgumentNullException.ThrowIfNull(clock);
		this.http = http;
		this.clock = clock;
	}

	public async Task<Result<ModImage>> GetAsync(string address, CancellationToken ct)
	{
		if (ModDbClient.TrustedDownload(address ?? string.Empty) is not { } url)
		{
			return Failure.Invalid("Картинка доступна только с сервера ModDB по https");
		}

		var key = url.AbsoluteUri;
		lock (gate)
		{
			if (cache.TryGetValue(key, out var hit))
			{
				cache[key] = (hit.Image, clock.GetTimestamp());
				return hit.Image;
			}
		}

		var fetched = await FetchAsync(url, ct).ConfigureAwait(false);
		if (fetched.IsSuccess)
		{
			Remember(key, fetched.Value);
		}

		return fetched;
	}

	/// <summary>Тип по первым байтам; <c>null</c>, если это не поддерживаемая картинка.</summary>
	public static string? Sniff(ReadOnlySpan<byte> data)
	{
		if (data.Length >= 8 && data[..8].SequenceEqual((byte[]) [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
		{
			return "image/png";
		}

		if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
		{
			return "image/jpeg";
		}

		if (data.Length >= 6 && data[..4].SequenceEqual("GIF8"u8) && data[4] is (byte) '7' or (byte) '9' && data[5] == (byte) 'a')
		{
			return "image/gif";
		}

		if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
		{
			return "image/webp";
		}

		return null;
	}

	private async Task<Result<ModImage>> FetchAsync(Uri url, CancellationToken ct)
	{
		var limit = TimeSpan.FromSeconds(10);
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(limit);
		try
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, url);
			request.Headers.UserAgent.ParseAdd(userAgent);
			using var response = await http
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
				.ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return Failure.Unavailable($"Картинка недоступна: HTTP {(int) response.StatusCode}");
			}

			var declared = response.Content.Headers.ContentType?.MediaType;
			if (Allowed(declared) is null)
			{
				return Failure.Unavailable("Сервер ModDB вернул не картинку");
			}

			if (response.Content.Headers.ContentLength > MaxImageBytes)
			{
				return Failure.Unavailable("Картинка слишком большая");
			}

			await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
			using var buffer = new MemoryStream();
			var chunk = new byte[16 * 1024];
			int read;
			while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
			{
				if (buffer.Length + read > MaxImageBytes)
				{
					return Failure.Unavailable("Картинка слишком большая");
				}

				buffer.Write(chunk, 0, read);
			}

			var data = buffer.ToArray();
			return Sniff(data) is { } type
				? new ModImage(type, data)
				: Failure.Unavailable("Сервер ModDB вернул не картинку");
		}
		catch (Exception exception) when (exception is HttpRequestException or IOException
											  || (exception is OperationCanceledException && !ct.IsCancellationRequested))
		{
			return Failure.Unavailable($"Картинка недоступна: {Reachability.Explain(exception, url, limit)}");
		}
	}

	private static string? Allowed(string? mediaType) => mediaType?.Trim().ToLowerInvariant() switch
	{
		"image/png" => "image/png",
		"image/jpeg" or "image/jpg" => "image/jpeg",
		"image/gif" => "image/gif",
		"image/webp" => "image/webp",
		_ => null,
	};

	private void Remember(string key, ModImage image)
	{
		lock (gate)
		{
			if (cache.Remove(key, out var old))
			{
				cached -= old.Image.Data.Length;
			}

			cache[key] = (image, clock.GetTimestamp());
			cached += image.Data.Length;

			// Вытесняются давно не запрашивавшиеся, пока общий размер не уложится в предел.
			while (cached > CacheBytes && cache.Count > 1)
			{
				var oldest = cache.MinBy(item => item.Value.At).Key;
				cached -= cache[oldest].Image.Data.Length;
				cache.Remove(oldest);
			}
		}
	}
}
