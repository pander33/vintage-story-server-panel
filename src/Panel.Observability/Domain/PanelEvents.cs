using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.SharedKernel;

namespace Panel.Observability.Domain;

/// <summary>Важность события. Порядок важен: уведомление отправляется начиная с порога.</summary>
public enum EventSeverity
{
	Info = 0,
	Warning = 1,
	Error = 2,
	Critical = 3,
}

public static class EventSeverityWire
{
	public static string ToWire(EventSeverity severity) => severity switch
	{
		EventSeverity.Info => "info",
		EventSeverity.Warning => "warning",
		EventSeverity.Error => "error",
		_ => "critical",
	};

	public static Result<EventSeverity> Parse(string? raw) => raw switch
	{
		"info" => EventSeverity.Info,
		"warning" => EventSeverity.Warning,
		"error" => EventSeverity.Error,
		"critical" => EventSeverity.Critical,
		_ => Failure.Invalid("Неизвестный уровень уведомлений"),
	};
}

/// <summary>
/// Событие панели, строка <c>events.jsonl</c>: <c>{id, ts, kind, severity, title, detail,
/// source}</c>.
/// </summary>
public sealed record PanelEvent(
	string Id,
	long Ts,
	string Kind,
	EventSeverity Severity,
	string Title,
	string Detail,
	string Source)
{
	/// <summary>
	/// Ключ паузы уведомлений: <c>dedupe_key</c> или вид события. В файл не пишется, нужен только
	/// для решения, отправлять ли уведомление снова.
	/// </summary>
	public string NotificationKey { get; init; } = string.Empty;

	private const int maxKind = 80;
	private const int maxTitle = 160;
	private const int maxDetail = 2000;
	private const int maxSource = 40;

	/// <summary>Новое событие; тексты обрезаются до пределов.</summary>
	public static Result<PanelEvent> Create(
		string kind,
		EventSeverity severity,
		string title,
		string detail,
		string source,
		DateTimeOffset now,
		string? dedupeKey = null)
	{
		if (string.IsNullOrEmpty(kind) || kind.Length > maxKind)
		{
			return Failure.Invalid("Недопустимое событие");
		}

		return new PanelEvent(
			Guid.NewGuid().ToString("N"),
			now.ToUnixTimeSeconds(),
			kind,
			severity,
			Clip(title, maxTitle),
			Clip(detail, maxDetail),
			Clip(source, maxSource))
		{
			NotificationKey = string.IsNullOrEmpty(dedupeKey) ? kind : dedupeKey,
		};
	}

	public JsonObject ToJson() => new()
	{
		["id"] = Id,
		["ts"] = Ts,
		["kind"] = Kind,
		["severity"] = EventSeverityWire.ToWire(Severity),
		["title"] = Title,
		["detail"] = Detail,
		["source"] = Source,
	};

	private static string Clip(string? value, int maximum) =>
		value is null ? string.Empty : value.Length > maximum ? value[..maximum] : value;
}

/// <summary>Фильтр ленты событий: важность и подстрока в заголовке или подробностях.</summary>
public sealed record EventFilter
{
	private EventFilter(string? severity, string needle)
	{
		Severity = severity;
		Needle = needle;
	}

	public string? Severity { get; }

	private string Needle { get; }

	/// <summary>Проверка фильтра; текст отказа для интерфейса.</summary>
	public static Result<EventFilter> Create(string? severity, string? search)
	{
		var value = severity ?? "all";
		var text = search ?? string.Empty;

		if (value is not ("all" or "info" or "warning" or "error" or "critical") || text.EnumerateRunes().Count() > 120)
		{
			return Failure.Invalid("Недопустимый фильтр событий");
		}

		return new EventFilter(value == "all" ? null : value, text.Trim().ToLowerInvariant());
	}

	/// <summary>
	/// <c>recent()</c>: записи из хвоста, новые первыми, не старше окна хранения, затем фильтр и
	/// предел.
	/// </summary>
	public IReadOnlyList<JsonObject> Apply(IEnumerable<JsonObject> newestFirst, int limit)
	{
		ArgumentNullException.ThrowIfNull(newestFirst);

		return [.. newestFirst
			.Where(item =>
				(Severity is null || TelemetryNumbers.Text(item["severity"]) == Severity)
				&& (Needle.Length == 0
					|| (TelemetryNumbers.Text(item["title"]) + " " + TelemetryNumbers.Text(item["detail"]))
						.ToLowerInvariant()
						.Contains(Needle, StringComparison.Ordinal)))
			.Take(limit)];
	}
}

/// <summary>
/// Настройки уведомлений: <c>notifications.json</c>, общий для всех серверов панели.
/// </summary>
/// <remarks>
/// Документ хранится целиком, неизвестные этой версии ключи сохраняются. Секреты (адреса
/// вебхуков и токен Telegram) наружу не отдаются: <see cref="Public"/> сообщает только,
/// заданы ли они.
/// </remarks>
public sealed class NotificationSettings
{
	private static readonly string[] secretKeys = ["webhook_url", "discord_url", "telegram_token", "telegram_chat_id"];

	private readonly JsonObject document;

	private NotificationSettings(JsonObject document) => this.document = document;

	public bool Enabled => TelemetryNumbers.Truthy(document["enabled"]);

	public EventSeverity MinimumSeverity =>
		EventSeverityWire.Parse(TelemetryNumbers.Text(document["minimum_severity"])) is { IsSuccess: true } parsed
			? parsed.Value
			: EventSeverity.Warning;

	public int CooldownSeconds => (int) TelemetryNumbers.Number(document["cooldown_seconds"], 300);

	public string WebhookUrl => Secret("webhook_url");

	public string DiscordUrl => Secret("discord_url");

	public string TelegramToken => Secret("telegram_token");

	public string TelegramChatId => Secret("telegram_chat_id");

	/// <summary>Все заданные секреты, чтобы убрать их из текста ошибки доставки.</summary>
	public IEnumerable<string> Secrets => secretKeys.Select(Secret).Where(value => value.Length > 0);

	public static NotificationSettings Defaults => FromJson(null);

	/// <summary>Файл поверх умолчаний, как <c>DEFAULTS | read_json(...)</c>.</summary>
	public static NotificationSettings FromJson(JsonNode? stored)
	{
		var document = new JsonObject
		{
			["enabled"] = false,
			["minimum_severity"] = "warning",
			["cooldown_seconds"] = 300,
			["webhook_url"] = string.Empty,
			["discord_url"] = string.Empty,
			["telegram_token"] = string.Empty,
			["telegram_chat_id"] = string.Empty,
		};

		if (stored is JsonObject values)
		{
			foreach (var (key, value) in values)
			{
				document[key] = value?.DeepClone();
			}
		}

		return new NotificationSettings(document);
	}

	public JsonObject ToJson() => (JsonObject) document.DeepClone();

	/// <summary>
	/// Новые настройки. Секрет, который не передан (<c>null</c>), остаётся прежним: форма не знает
	/// старого значения и не должна его стирать.
	/// </summary>
	public Result<NotificationSettings> Apply(NotificationUpdate update)
	{
		ArgumentNullException.ThrowIfNull(update);

		var severity = EventSeverityWire.Parse(update.MinimumSeverity ?? EventSeverityWire.ToWire(MinimumSeverity));
		if (!severity.IsSuccess)
		{
			return Result.Fail<NotificationSettings>(severity.Failure!);
		}

		var cooldown = update.CooldownSeconds ?? CooldownSeconds;
		if (cooldown is < 30 or > 86400)
		{
			return Failure.Invalid("Пауза уведомлений должна быть от 30 до 86400 секунд");
		}

		var document = ToJson();
		document["enabled"] = update.Enabled;
		document["minimum_severity"] = EventSeverityWire.ToWire(severity.Value);
		document["cooldown_seconds"] = cooldown;

		foreach (var (key, raw) in new[]
		{
			("webhook_url", update.WebhookUrl),
			("discord_url", update.DiscordUrl),
			("telegram_token", update.TelegramToken),
			("telegram_chat_id", update.TelegramChatId),
		})
		{
			if (raw is null)
			{
				continue;
			}

			var value = raw.Trim();
			if (value.EnumerateRunes().Count() > 1000 || value.Any(symbol => symbol < ' '))
			{
				return Failure.Invalid($"Недопустимое значение {key}");
			}

			if (key.EndsWith("_url", StringComparison.Ordinal) && value.Length > 0
				&& !value.StartsWith("https://", StringComparison.Ordinal))
			{
				return Failure.Invalid("Webhook должен использовать HTTPS");
			}

			document[key] = value;
		}

		return new NotificationSettings(document);
	}

	/// <summary>Данные для формы: пороги и признаки «задано» вместо секретов.</summary>
	public JsonObject Public() => new()
	{
		["enabled"] = document["enabled"]?.DeepClone(),
		["minimum_severity"] = document["minimum_severity"]?.DeepClone(),
		["cooldown_seconds"] = document["cooldown_seconds"]?.DeepClone(),
		["webhook_url_configured"] = WebhookUrl.Length > 0,
		["discord_url_configured"] = DiscordUrl.Length > 0,
		["telegram_token_configured"] = TelegramToken.Length > 0,
		["telegram_chat_id_configured"] = TelegramChatId.Length > 0,
	};

	private string Secret(string key) =>
		document[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String
			? value.GetValue<string>()
			: TelemetryNumbers.Truthy(document[key]) ? TelemetryNumbers.Text(document[key]) : string.Empty;
}

/// <summary>Тело <c>PUT /api/notifications</c> в доменных терминах.</summary>
public sealed record NotificationUpdate(
	bool Enabled,
	string? MinimumSeverity,
	int? CooldownSeconds,
	string? WebhookUrl,
	string? DiscordUrl,
	string? TelegramToken,
	string? TelegramChatId);

/// <summary>
/// Решение, отправлять ли уведомление: уведомления включены, важность не ниже порога, и с
/// прошлого уведомления с тем же ключом прошла пауза.
/// </summary>
/// <remarks>
/// Пауза отсчитывается для каждого сервера отдельно: одинаковое событие двух серверов даст
/// два уведомления.
/// </remarks>
public sealed class NotificationGate
{
	private readonly Dictionary<string, long> last = new(StringComparer.Ordinal);

	public bool Admit(PanelEvent record, bool notify, NotificationSettings settings)
	{
		ArgumentNullException.ThrowIfNull(record);
		ArgumentNullException.ThrowIfNull(settings);

		if (!notify || !settings.Enabled || record.Severity < settings.MinimumSeverity)
		{
			return false;
		}

		var key = record.NotificationKey.Length > 0 ? record.NotificationKey : record.Kind;
		lock (last)
		{
			if (record.Ts - last.GetValueOrDefault(key) < settings.CooldownSeconds)
			{
				return false;
			}

			last[key] = record.Ts;
			return true;
		}
	}
}

/// <summary>Одна доставка: куда и что отправить.</summary>
public sealed record Delivery(string Url, JsonObject Payload);

/// <summary>
/// Сообщение о событии для каждого канала.
/// </summary>
public static class NotificationMessage
{
	public static IReadOnlyList<Delivery> For(JsonObject record, NotificationSettings settings)
	{
		ArgumentNullException.ThrowIfNull(record);
		ArgumentNullException.ThrowIfNull(settings);

		var text = $"[{TelemetryNumbers.Text(record["severity"]).ToUpperInvariant()}] {TelemetryNumbers.Text(record["title"])}";
		if (TelemetryNumbers.Truthy(record["detail"]))
		{
			text += "\n" + TelemetryNumbers.Text(record["detail"]);
		}

		var deliveries = new List<Delivery>();
		if (settings.WebhookUrl.Length > 0)
		{
			deliveries.Add(new Delivery(settings.WebhookUrl, (JsonObject) record.DeepClone()));
		}

		if (settings.DiscordUrl.Length > 0)
		{
			deliveries.Add(new Delivery(settings.DiscordUrl, new JsonObject { ["content"] = Clip(text, 1900) }));
		}

		if (settings.TelegramToken.Length > 0 && settings.TelegramChatId.Length > 0)
		{
			deliveries.Add(new Delivery(
				"https://api.telegram.org/bot" + settings.TelegramToken + "/sendMessage",
				new JsonObject { ["chat_id"] = settings.TelegramChatId, ["text"] = Clip(text, 4000) }));
		}

		return deliveries;
	}

	/// <summary>Тестовое сообщение формы «Проверить отправку».</summary>
	public static JsonObject Test(DateTimeOffset now) => new()
	{
		["title"] = "Тест Vintage Story Panel",
		["detail"] = "Канал уведомлений работает.",
		["severity"] = "info",
		["kind"] = "notification.test",
		["ts"] = now.ToUnixTimeSeconds(),
	};

	private static string Clip(string value, int maximum) => value.Length > maximum ? value[..maximum] : value;
}
