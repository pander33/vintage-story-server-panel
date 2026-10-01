using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Contracts;
using Panel.Observability.Domain;
using Panel.SharedKernel;

namespace Panel.Observability.Infrastructure;

/// <summary>
/// Лента событий одного сервера: <c>instances/&lt;id&gt;/events.jsonl</c>.
/// </summary>
/// <remarks>
/// Хранятся записи за <c>PANEL_EVENT_RETENTION_DAYS</c> дней, не больше 5000, по правилам
/// <see cref="JsonLinesFile"/>.
/// </remarks>
public sealed class EventLog
{
	private readonly JsonLinesFile file;
	private readonly int retentionDays;

	public EventLog(string path, int retentionDays, TimeProvider clock)
	{
		this.retentionDays = retentionDays;
		file = new JsonLinesFile(path, keep: 5000, retentionDays: retentionDays, time: clock);
	}

	public Task<Result<Done>> AppendAsync(JsonObject record, CancellationToken ct) =>
		file.AppendAsync(record.ToJsonString(PanelJson.Options), ct);

	/// <summary>
	/// Последние события, новые первыми. Хвост берётся с запасом (вчетверо больше предела, но не
	/// больше тысячи), потому что часть отсеет фильтр.
	/// </summary>
	public async Task<IReadOnlyList<JsonObject>> RecentAsync(
		EventFilter filter,
		int limit,
		DateTimeOffset now,
		CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(filter);

		var window = Math.Clamp(Math.Max(limit * 4, limit), 1, 1000);
		var cutoff = now.ToUnixTimeSeconds() - (long) retentionDays * 86400;
		var lines = await file.TailAsync(window, ct).ConfigureAwait(false);

		var newestFirst = lines
			.Reverse()
			.Select(Parse)
			.OfType<JsonObject>()
			.Where(item => (long) TelemetryNumbers.Number(item["ts"] ?? item["started_at"]) >= cutoff);

		return filter.Apply(newestFirst, limit);
	}

	private static JsonObject? Parse(string line)
	{
		try
		{
			return JsonNode.Parse(line) as JsonObject;
		}
		catch (JsonException)
		{
			return null;
		}
	}
}

/// <summary>
/// <c>notifications.json</c> в каталоге состояния панели, один файл на все серверы.
/// </summary>
/// <remarks>
/// Перед каждым использованием файл перечитывается, если изменился: настройки меняют через
/// любой сервер, а действуют они для всех.
/// </remarks>
public sealed class NotificationSettingsFile
{
	private readonly string path;
	private readonly Lock sync = new();
	private NotificationSettings cached = NotificationSettings.Defaults;
	private DateTime stamp = DateTime.MinValue;

	public NotificationSettingsFile(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		this.path = path;
	}

	public NotificationSettings Current
	{
		get
		{
			var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
			lock (sync)
			{
				if (stamp != this.stamp)
				{
					cached = NotificationSettings.FromJson(Read());
					this.stamp = stamp;
				}

				return cached;
			}
		}
	}

	/// <summary>
	/// Запись целиком и атомарно, права 0600: в файле токен бота и адреса вебхуков.
	/// </summary>
	public async Task<Result<Done>> SaveAsync(NotificationSettings settings, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(settings);

		var written = await AtomicFile
			.WriteAsync(path, settings.ToJson().ToJsonString(PanelJson.Indented) + "\n", ct)
			.ConfigureAwait(false);

		if (written.IsSuccess && !OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}

		return written;
	}

	private JsonNode? Read()
	{
		try
		{
			return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return null;
		}
	}
}

/// <summary>
/// Отправка уведомлений: POST JSON на каждый заданный канал.
/// </summary>
/// <remarks>
/// Только HTTPS: адреса проверены при сохранении настроек, а адрес Telegram собирается здесь
/// из токена. Из текста ошибки убираются секреты: исключение HTTP-клиента часто содержит
/// адрес, а в адресе Telegram есть токен бота.
/// </remarks>
public sealed class NotificationSender
{
	private const string userAgent = "vs-panel/0.2.0";

	private static readonly TimeSpan timeout = TimeSpan.FromSeconds(12);

	private readonly HttpClient http;

	public NotificationSender(HttpClient http)
	{
		ArgumentNullException.ThrowIfNull(http);
		this.http = http;
	}

	/// <param name="render">Текст фразы на языке уведомлений (<see cref="NotificationSettings.Language"/>).</param>
	public async Task<Result<Done>> SendAsync(JsonObject record, NotificationSettings settings, Func<Phrase, string> render, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(record);
		ArgumentNullException.ThrowIfNull(settings);

		var deliveries = NotificationMessage.For(record, settings, render);
		if (deliveries.Count == 0)
		{
			return Failure.Unavailable("Канал уведомлений не настроен");
		}

		foreach (var delivery in deliveries)
		{
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
			timeout.CancelAfter(NotificationSender.timeout);

			try
			{
				using var request = new HttpRequestMessage(HttpMethod.Post, delivery.Url)
				{
					Content = JsonContent.Create(delivery.Payload, options: PanelJson.Options),
				};
				request.Headers.UserAgent.ParseAdd(userAgent);

				using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
				if ((int) response.StatusCode >= 300)
				{
					return Failure.Unavailable(Redact($"HTTP {(int) response.StatusCode}", settings));
				}
			}
			catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
			{
				return Failure.Unavailable(Redact(exception.Message, settings));
			}
		}

		return Done.Ok;
	}

	private static string Redact(string error, NotificationSettings settings) =>
		settings.Secrets.Aggregate(error, (text, secret) => text.Replace(secret, "[redacted]", StringComparison.Ordinal));
}
