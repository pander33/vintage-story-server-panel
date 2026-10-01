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
	private readonly Lock sync = new();
	private CatalogueSnapshot? cached;
	private bool loaded;
	private Task<Result<CatalogueSnapshot>>? updating;

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
		ct.ThrowIfCancellationRequested();
		Task<Result<CatalogueSnapshot>> task;
		TaskCompletionSource<Result<CatalogueSnapshot>>? completion = null;
		lock (sync)
		{
			if (!loaded)
			{
				cached = ReadCache();
				loaded = true;
			}

			if (updating is { } running)
			{
				task = running;
			}
			else if (cached is { } fresh && !refresh && clock.GetUtcNow().ToUnixTimeSeconds() - fresh.FetchedAt < ttlSeconds)
			{
				return fresh with { FromNetwork = false, Stale = false };
			}
			else
			{
				completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
				task = updating = completion.Task;
			}
		}

		if (completion is not null)
		{
			_ = RefreshAsync(completion);
		}

		// Отмена одного зрителя не отменяет общий запрос, у сетевого fetch свой таймаут.
		return await task.WaitAsync(ct).ConfigureAwait(false);
	}

	private async Task RefreshAsync(TaskCompletionSource<Result<CatalogueSnapshot>> completion)
	{
		try
		{
			var fetched = await fetch(CancellationToken.None).ConfigureAwait(false);
			Result<CatalogueSnapshot> result;
			if (fetched.IsSuccess)
			{
				var now = clock.GetUtcNow().ToUnixTimeSeconds();
				var snapshot = Present(fetched.Value, now, fromNetwork: true, stale: false);
				await WriteCacheAsync(fetched.Value, now, CancellationToken.None).ConfigureAwait(false);
				lock (sync)
				{
					cached = snapshot;
				}
				result = snapshot;
			}
			else
			{
				lock (sync)
				{
					result = cached is { } old
						? old with { FromNetwork = false, Stale = true }
						: Result.Fail<CatalogueSnapshot>(fetched.Failure!);
				}
			}
			lock (sync)
			{
				updating = null;
				completion.SetResult(result);
			}
		}
		catch (Exception exception)
		{
			lock (sync)
			{
				updating = null;
				completion.SetException(exception);
			}
		}
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
		var limit = TimeSpan.FromSeconds(20);
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(limit);

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
			return Failure.Unavailable($"Каталог версий недоступен: {Reachability.Explain(exception, new Uri(ManifestUrl), limit)}");
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

	private CatalogueSnapshot Present(JsonObject raw, long fetchedAt, bool fromNetwork, bool stale) =>
		new(Array.AsReadOnly(ReleaseManifest.Parse(raw, Platform).ToArray()), fetchedAt, fromNetwork, stale, Platform);

	private CatalogueSnapshot? ReadCache()
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
			return Present(raw, fetchedAt, fromNetwork: false, stale: false);
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
