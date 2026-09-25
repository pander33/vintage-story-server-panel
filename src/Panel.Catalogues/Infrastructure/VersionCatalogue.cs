using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Catalogues.Domain;
using Panel.SharedKernel;

namespace Panel.Catalogues.Infrastructure;

/// <summary>
/// Каталог серверных пакетов с файловым кэшем, один на процесс панели.
/// </summary>
/// <remarks>
/// Кэш на сутки в <c>catalogue.json</c> (<c>{"fetched_at", "raw"}</c>). Если сеть недоступна,
/// возвращается устаревший кэш с флагом <c>stale</c>: список версий недельной давности лучше,
/// чем мастер установки, который не может начаться.
/// </remarks>
public sealed class VersionCatalogue
{
	public const string ManifestUrl = "https://api.vintagestory.at/stable-unstable.json";

	private const int maxManifestBytes = 8 * 1024 * 1024;

	private readonly string cacheFile;
	private readonly Func<CancellationToken, Task<Result<JsonObject>>> fetch;
	private readonly TimeProvider clock;
	private readonly long ttlSeconds;

	public VersionCatalogue(
		string cacheFile,
		Func<CancellationToken, Task<Result<JsonObject>>> fetch,
		TimeProvider clock,
		ServerPlatform platform = ServerPlatform.Linux,
		long ttlSeconds = 86_400)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(cacheFile);
		ArgumentNullException.ThrowIfNull(fetch);
		ArgumentNullException.ThrowIfNull(clock);
		this.cacheFile = cacheFile;
		this.fetch = fetch;
		this.clock = clock;
		this.ttlSeconds = ttlSeconds;
		Platform = platform;
	}

	public ServerPlatform Platform { get; }

	public async Task<Result<CatalogueSnapshot>> SnapshotAsync(bool refresh, CancellationToken ct)
	{
		var cached = ReadCache();
		var now = clock.GetUtcNow().ToUnixTimeSeconds();
		if (cached is { } fresh && !refresh && now - fresh.FetchedAt < ttlSeconds)
		{
			return Present(fresh.Raw, fresh.FetchedAt, fromNetwork: false, stale: false);
		}

		var fetched = await fetch(ct).ConfigureAwait(false);
		if (!fetched.IsSuccess)
		{
			// Сеть недоступна, кэш устарел: каталог отдаётся с пометкой.
			return cached is { } old
				? Present(old.Raw, old.FetchedAt, fromNetwork: false, stale: true)
				: Result.Fail<CatalogueSnapshot>(fetched.Failure!);
		}

		await WriteCacheAsync(fetched.Value, now, ct).ConfigureAwait(false);
		return Present(fetched.Value, now, fromNetwork: true, stale: false);
	}

	/// <summary>Пакет нужной версии; если его нет в каталоге, отказ с номером версии.</summary>
	public async Task<Result<GamePackage>> FindAsync(string version, bool refresh, CancellationToken ct)
	{
		var snapshot = await SnapshotAsync(refresh, ct).ConfigureAwait(false);
		if (!snapshot.IsSuccess)
		{
			return Result.Fail<GamePackage>(snapshot.Failure!);
		}

		return snapshot.Value.Versions.FirstOrDefault(package => package.Version.Text == version) is { } found
			? found
			: Failure.Unavailable($"Версия {version} отсутствует в каталоге");
	}

	/// <summary>Манифест из сети: не больше 8 МБ, непустой объект.</summary>
	public static Func<CancellationToken, Task<Result<JsonObject>>> FromNetwork(HttpClient http) => async ct =>
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(TimeSpan.FromSeconds(20));

		byte[] data;
		try
		{
			using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
			request.Headers.UserAgent.ParseAdd("VintageStoryPanel/1.0");
			using var response = await http
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
				.ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				return Failure.Unavailable(
					$"Каталог версий недоступен: HTTP Error {(int) response.StatusCode}: {response.ReasonPhrase}");
			}

			await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
			// Читается не больше предела плюс один байт, сколько бы ни прислали.
			using var buffer = new MemoryStream();
			var chunk = new byte[81920];
			int read;
			while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
			{
				buffer.Write(chunk, 0, read);
				if (buffer.Length > maxManifestBytes)
				{
					return Failure.Unavailable("Каталог версий слишком большой");
				}
			}

			data = buffer.ToArray();
		}
		catch (Exception exception) when (exception is HttpRequestException or IOException
											  || (exception is OperationCanceledException && !ct.IsCancellationRequested))
		{
			return Failure.Unavailable($"Каталог версий недоступен: {exception.Message}");
		}

		try
		{
			return JsonNode.Parse(data) is JsonObject { Count: > 0 } manifest
				? manifest
				: Failure.Unavailable("Каталог версий пуст");
		}
		catch (JsonException)
		{
			return Failure.Unavailable("Каталог версий вернул некорректный JSON");
		}
	};

	private Result<CatalogueSnapshot> Present(JsonObject raw, long fetchedAt, bool fromNetwork, bool stale) =>
		new CatalogueSnapshot(ReleaseManifest.Parse(raw, Platform), fetchedAt, fromNetwork, stale, Platform);

	private (JsonObject Raw, long FetchedAt)? ReadCache()
	{
		try
		{
			if (JsonNode.Parse(File.ReadAllText(cacheFile)) is not JsonObject { } cache
				|| cache["raw"] is not JsonObject raw)
			{
				return null;
			}

			// int(cached.get("fetched_at", 0)): не число означает кэш без возраста, то есть устаревший.
			var fetchedAt = cache["fetched_at"] is JsonValue stamp && stamp.TryGetValue<double>(out var seconds)
				? (long) seconds
				: 0;
			return ((JsonObject) raw.DeepClone(), fetchedAt);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return null;
		}
	}

	/// <summary>Ошибка записи кэша не мешает: каталог уже получен, в следующий раз он придёт из сети.</summary>
	private async Task WriteCacheAsync(JsonObject raw, long fetchedAt, CancellationToken ct)
	{
		var payload = new JsonObject { ["fetched_at"] = fetchedAt, ["raw"] = raw.DeepClone() };
		var directory = Path.GetDirectoryName(cacheFile);
		try
		{
			if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
			{
				if (OperatingSystem.IsWindows())
				{
					Directory.CreateDirectory(directory);
				}
				else
				{
					Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return;
		}

		await AtomicFile.WriteAsync(
			cacheFile,
			payload.ToJsonString(Panel.Contracts.PanelJson.Options),
			ct).ConfigureAwait(false);
	}
}
