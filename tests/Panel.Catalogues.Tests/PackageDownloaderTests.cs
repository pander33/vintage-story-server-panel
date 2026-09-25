using System.Net;
using System.Security.Cryptography;
using Panel.Catalogues.Domain;
using Panel.Catalogues.Infrastructure;

namespace Panel.Catalogues.Tests;

/// <summary>Скачанный пакет сервера проверяется.</summary>
public sealed class PackageDownloaderTests : IDisposable
{
	private static readonly byte[] package = "server package"u8.ToArray();

	private readonly string directory = Path.Combine(Path.GetTempPath(), "vs-download-" + Guid.NewGuid().ToString("n"));
	private int requests;

	public void Dispose()
	{
		if (Directory.Exists(directory))
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	private string Target => Path.Combine(directory, "game-1.tar.gz");

	private static GamePackage Release(string? md5 = null, string url = "https://cdn.vintagestory.at/gamefiles/stable/vs_server.tar.gz") => new(
		ManifestVersion.Parse("1.22.7")!.Value,
		"vs_server.tar.gz",
		"1 MB",
		package.Length,
		Digest.Parse(md5 ?? Convert.ToHexString(MD5.HashData(package))),
		new Uri(url),
		Latest: true);

	private PackageDownloader Downloader(byte[]? body = null) => new(new HttpClient(new Handler(_ =>
	{
		requests++;
		return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body ?? package) };
	})));

	[Fact]
	public async Task Пакет_со_своей_суммой_ложится_на_диск()
	{
		var written = await Downloader().DownloadAsync(Release(), Target, 1024, CancellationToken.None);

		Assert.Equal(package.Length, written.Value);
		Assert.Equal(package, await File.ReadAllBytesAsync(Target));
	}

	[Fact]
	public async Task Несовпавшая_сумма_удаляет_файл()
	{
		var refused = await Downloader("tampered package"u8.ToArray()).DownloadAsync(Release(), Target, 1024, CancellationToken.None);

		Assert.Equal("Контрольная сумма пакета не совпала с каталогом", refused.Failure!.Message);
		Assert.False(File.Exists(Target));
	}

	[Fact]
	public async Task Больше_предела_не_скачивается()
	{
		var refused = await Downloader().DownloadAsync(Release(), Target, 4, CancellationToken.None);

		Assert.Equal("Пакет сервера превышает лимит панели", refused.Failure!.Message);
		Assert.False(File.Exists(Target));
	}

	[Fact]
	public async Task Чужой_хост_и_пакет_без_суммы_не_запрашиваются_вовсе()
	{
		var foreign = await Downloader().DownloadAsync(Release(url: "https://example.com/vs_server.tar.gz"), Target, 1024, CancellationToken.None);
		var unsigned = await Downloader().DownloadAsync(Release(md5: "not-a-digest"), Target, 1024, CancellationToken.None);

		Assert.Equal("Недопустимый адрес загрузки", foreign.Failure!.Message);
		Assert.Equal("В каталоге нет контрольной суммы для этой версии", unsigned.Failure!.Message);
		Assert.Equal(0, requests);
	}

	private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(respond(request));
	}
}
