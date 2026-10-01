using System.Security.Cryptography;
using Panel.Catalogues.Domain;
using Panel.SharedKernel;

namespace Panel.Catalogues.Infrastructure;

/// <summary>
/// Скачивание пакета сервера с проверкой по манифесту.
/// </summary>
/// <remarks>
/// <para>
/// Проверяются HTTPS, хост из списка манифеста, предел размера по заголовку и при чтении,
/// а в конце md5 из манифеста. Пакет без суммы не скачивается. При любом отказе
/// недописанный файл удаляется.
/// </para>
/// <para>
/// Пакет пишется на диск кусками по мегабайту: это сотни мегабайт, а панель работает на
/// той же машине, что и игра.
/// </para>
/// </remarks>
public sealed class PackageDownloader
{
	/// <summary>Предел размера пакета; сам пакет весит несколько сотен мегабайт.</summary>
	public const long MaxPackageBytes = 4L * 1024 * 1024 * 1024;

	private readonly HttpClient http;

	public PackageDownloader(HttpClient http)
	{
		ArgumentNullException.ThrowIfNull(http);
		this.http = http;
	}

	/// <summary>Скачать в <paramref name="destination"/> и вернуть число байт.</summary>
	public async Task<Result<long>> DownloadAsync(GamePackage package, string destination, long maximum, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(package);
		ArgumentException.ThrowIfNullOrWhiteSpace(destination);

		// Хост уже проверен при разборе манифеста, но адрес мог прийти и не оттуда.
		if (ReleaseManifest.ParseTrustedUrl(package.Url.OriginalString) is not { } url)
		{
			return Failure.Unavailable("Недопустимый адрес загрузки");
		}

		if (package.Md5 is not { } expected)
		{
			return Failure.Unavailable("В каталоге нет контрольной суммы для этой версии");
		}

		var written = 0L;
		using var digest = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
		try
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, url);
			request.Headers.UserAgent.ParseAdd("VintageStoryPanel/1.0");
			using var response = await http
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
				.ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return Fail(destination, Phrase.Format($"Не удалось скачать пакет сервера: HTTP Error {(int) response.StatusCode}: {response.ReasonPhrase}"));
			}

			if (response.Content.Headers.ContentLength is { } declared && declared > maximum)
			{
				return Fail(destination, Phrase.From("Пакет сервера превышает лимит панели"));
			}

			var directory = Path.GetDirectoryName(destination);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
			await using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				var buffer = new byte[1024 * 1024];
				int read;
				while ((read = await ReadAsync(source, buffer, ct).ConfigureAwait(false)) > 0)
				{
					written += read;
					if (written > maximum)
					{
						break;
					}

					digest.AppendData(buffer, 0, read);
					await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
				}
			}

			if (written > maximum)
			{
				return Fail(destination, Phrase.From("Пакет сервера превышает лимит панели"));
			}

			if (!OperatingSystem.IsWindows())
			{
				File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			}
		}
		catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException
											  || (exception is OperationCanceledException && !ct.IsCancellationRequested))
		{
			return Fail(destination, Phrase.Format($"Не удалось скачать пакет сервера: {Reachability.Explain(exception, url, http.Timeout)}"));
		}
		catch
		{
			Delete(destination);
			throw;
		}

		if (!string.Equals(Convert.ToHexString(digest.GetHashAndReset()), expected.Hex, StringComparison.OrdinalIgnoreCase))
		{
			return Fail(destination, Phrase.From("Контрольная сумма пакета не совпала с каталогом"));
		}

		return written;
	}

	/// <summary>Чтение куска с таймаутом, чтобы зависшее соединение не держало операцию бесконечно.</summary>
	private static async Task<int> ReadAsync(Stream source, byte[] buffer, CancellationToken ct)
	{
		using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
		idle.CancelAfter(TimeSpan.FromSeconds(60));
		return await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
	}

	private static Failure Fail(string destination, Phrase message)
	{
		Delete(destination);
		return Failure.Unavailable(message);
	}

	private static void Delete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Ошибка уборки не должна заменять исходный отказ.
		}
	}
}
