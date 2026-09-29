using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Panel.Cli;

/// <summary>
/// Пакет панели из GitHub Releases: <c>vs-panel install|update</c> без <c>--from</c>.
/// </summary>
/// <remarks>
/// Релиз с тегом <c>v&lt;версия&gt;</c> несёт архив на каждую цель и <c>SHA256SUMS</c>
/// (задание <c>release</c> в CI); <c>deploy/get.sh</c> и <c>deploy/windows/get.ps1</c> читают
/// тот же релиз. Тег выбирается один раз, и архив с суммой берутся из него: адрес
/// <c>latest/download</c> мог бы отдать их из разных релизов, если новый выйдет посередине.
/// Сумма из того же релиза защищает от порчи при загрузке, не от подмены релиза.
/// </remarks>
internal static class ReleaseSource
{
	public const string Repository = "ruslan120p/vintage-story-panel-net";

	public const string ChecksumFile = "SHA256SUMS";

	/// <summary>
	/// Цель этой машины; <see langword="null"/> — пакета для неё нет. Пакета <c>win-arm64</c>
	/// CI не собирает, а <c>win-x64</c> на ARM64 не примет установщик (<c>CheckPlatform</c>).
	/// </summary>
	public static string? Rid(bool windows, Architecture architecture) => (windows, architecture) switch
	{
		(false, Architecture.X64) => "linux-x64",
		(false, Architecture.Arm64) => "linux-arm64",
		(true, Architecture.X64) => "win-x64",
		_ => null,
	};

	/// <summary>
	/// Порядок версий: числа сравниваются как числа, пререлиз (<c>0.3.0-rc1</c>) младше своего
	/// релиза, метка сборки после <c>+</c> не учитывается.
	/// </summary>
	public static int CompareVersions(string left, string right)
	{
		static (string Core, string Prerelease) Split(string version)
		{
			var bare = VersionOf(version).Split('+')[0];
			var dash = bare.IndexOf('-', StringComparison.Ordinal);
			return dash < 0 ? (bare, string.Empty) : (bare[..dash], bare[(dash + 1)..]);
		}

		var (leftCore, leftPrerelease) = Split(left);
		var (rightCore, rightPrerelease) = Split(right);
		var byCore = NaturalOrder.Instance.Compare(leftCore, rightCore);
		if (byCore != 0)
		{
			return byCore;
		}

		return (leftPrerelease.Length == 0, rightPrerelease.Length == 0) switch
		{
			(true, true) => 0,
			(true, false) => 1,
			(false, true) => -1,
			_ => NaturalOrder.Instance.Compare(leftPrerelease, rightPrerelease),
		};
	}

	/// <summary>Архив цели в релизе; внутри — каталог с тем же именем без расширения.</summary>
	public static string PackageName(string rid) =>
		rid.StartsWith("win-", StringComparison.Ordinal) ? $"vs-panel-{rid}.zip" : $"vs-panel-{rid}.tar.gz";

	public static string PackageDirectory(string rid) => $"vs-panel-{rid}";

	/// <summary>Тег релиза по версии: <c>0.3.0</c> и <c>v0.3.0</c> — один и тот же релиз.</summary>
	public static string? Tag(string version)
	{
		var bare = VersionOf(version.Trim());
		return ReleaseHistory.IsVersion(bare) && !bare.Contains('+', StringComparison.Ordinal) ? "v" + bare : null;
	}

	public static string VersionOf(string tag) => tag.StartsWith('v') ? tag[1..] : tag;

	public static Uri LatestUri { get; } = new($"https://api.github.com/repos/{Repository}/releases/latest");

	public static Uri AssetUri(string tag, string name) =>
		new($"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}");

	/// <summary>Тег из ответа <c>releases/latest</c>; пререлизы GitHub туда не включает.</summary>
	public static string? ParseLatestTag(string json)
	{
		try
		{
			using var document = JsonDocument.Parse(json);
			return document.RootElement.ValueKind == JsonValueKind.Object
				&& document.RootElement.TryGetProperty("tag_name", out var tag)
				&& tag.ValueKind == JsonValueKind.String
				&& Tag(tag.GetString()!) is { } parsed
					? parsed
					: null;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>
	/// Сумма файла из <c>SHA256SUMS</c> в формате <c>sha256sum</c>: <c>&lt;hex&gt;  имя</c> или
	/// <c>&lt;hex&gt; *имя</c>.
	/// </summary>
	public static string? ExpectedHash(string sums, string name)
	{
		foreach (var line in sums.Split('\n'))
		{
			var parts = line.Trim().Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 2
				&& parts[1].Trim().TrimStart('*') == name
				&& parts[0].Length == 64
				&& parts[0].All(char.IsAsciiHexDigit))
			{
				return parts[0].ToLowerInvariant();
			}
		}

		return null;
	}

	/// <summary>
	/// Скачать, сверить и распаковать пакет в <paramref name="downloads"/>. Каталог загрузок —
	/// внутри каталога программы: обычный пользователь не может подложить туда свой файл между
	/// проверкой суммы и запуском установщика, в отличие от временного каталога.
	/// </summary>
	public static async Task<DownloadedRelease> DownloadAsync(string tag, string rid, string downloads, TextWriter output)
	{
		ArgumentNullException.ThrowIfNull(output);
		var package = PackageName(rid);
		var work = Path.Combine(downloads, Guid.NewGuid().ToString("N"));
		DeleteDirectory(downloads);
		if (OperatingSystem.IsWindows())
		{
			Directory.CreateDirectory(work);
		}
		else
		{
			Directory.CreateDirectory(work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}

		var release = new DownloadedRelease(downloads, Path.Combine(work, PackageDirectory(rid)));
		try
		{
			using var client = Client();
			var archive = Path.Combine(work, package);
			output.WriteLine($"Downloading vs-panel {VersionOf(tag)} ({rid}) from GitHub");
			var sums = await client.GetStringAsync(AssetUri(tag, ChecksumFile)).ConfigureAwait(false);
			var expected = ExpectedHash(sums, package)
				?? throw new InstallFailed($"Release {tag} has no checksum for {package}");
			await DownloadFileAsync(client, AssetUri(tag, package), archive).ConfigureAwait(false);

			string actual;
			await using (var stream = File.OpenRead(archive))
			{
				actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
			}

			if (actual != expected)
			{
				throw new InstallFailed($"Checksum mismatch for {package}: expected {expected}, got {actual}. The download is corrupt; try again.");
			}

			await ExtractAsync(archive, work).ConfigureAwait(false);
			File.Delete(archive);
			var built = File.Exists(Path.Combine(release.Directory, Artifact.RidFile))
				? File.ReadLines(Path.Combine(release.Directory, Artifact.RidFile)).FirstOrDefault()?.Trim()
				: null;
			if (built != rid)
			{
				throw new InstallFailed($"{package} from release {tag} is not a {rid} panel package");
			}

			var artifact = Artifact.Open(release.Directory, windows: rid.StartsWith("win-", StringComparison.Ordinal));
			if (!artifact.IsSuccess || artifact.Value.Version != VersionOf(tag))
			{
				throw new InstallFailed($"{package} does not contain a complete panel package for version {VersionOf(tag)}");
			}

			return release;
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidDataException)
		{
			release.Dispose();
			throw new InstallFailed($"Could not download vs-panel {VersionOf(tag)}: {exception.Message}");
		}
		catch
		{
			release.Dispose();
			throw;
		}
	}

	/// <summary>Последний опубликованный релиз.</summary>
	public static async Task<string> LatestTagAsync()
	{
		try
		{
			using var client = Client();
			var json = await client.GetStringAsync(LatestUri).ConfigureAwait(false);
			return ParseLatestTag(json) ?? throw new InstallFailed($"No usable release tag in {LatestUri}");
		}
		catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
		{
			throw new InstallFailed($"No published release in {Repository}");
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
		{
			throw new InstallFailed($"Could not query the latest release on GitHub: {exception.Message}");
		}
	}

	private static HttpClient Client()
	{
		var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

		// Без User-Agent API GitHub отвечает 403.
		client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("vs-panel", Program.OwnVersion));
		return client;
	}

	private static async Task DownloadFileAsync(HttpClient client, Uri uri, string path)
	{
		using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
		await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
		await source.CopyToAsync(target).ConfigureAwait(false);
	}

	/// <summary>
	/// Распаковка штатными <see cref="TarFile"/> и <see cref="ZipFile"/>: оба отказывают записи,
	/// ведущей за пределы каталога. Архив к этому моменту уже сверен с суммой релиза.
	/// </summary>
	private static async Task ExtractAsync(string archive, string destination)
	{
		if (archive.EndsWith(".zip", StringComparison.Ordinal))
		{
			await ZipFile.ExtractToDirectoryAsync(archive, destination, overwriteFiles: false).ConfigureAwait(false);
			return;
		}

		await using var file = File.OpenRead(archive);
		await using var gzip = new GZipStream(file, CompressionMode.Decompress);
		await TarFile.ExtractToDirectoryAsync(gzip, destination, overwriteFiles: false).ConfigureAwait(false);
	}

	internal static void DeleteDirectory(string path)
	{
		if (Directory.Exists(path))
		{
			Directory.Delete(path, recursive: true);
		}
	}
}

/// <summary>Распакованный пакет релиза; каталог загрузок удаляется вместе с ним.</summary>
internal sealed class DownloadedRelease(string downloads, string directory) : IDisposable
{
	/// <summary>Каталог сборки для <c>--from</c>.</summary>
	public string Directory { get; } = directory;

	public void Dispose()
	{
		try
		{
			ReleaseSource.DeleteDirectory(downloads);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Остаток загрузки не мешает установке; следующая загрузка удалит его сама.
		}
	}
}
