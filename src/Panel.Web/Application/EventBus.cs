using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Panel.Contracts;
using Panel.Observability.Domain;
using Panel.Observability.Infrastructure;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Шина событий: запись в ленту сервера и при необходимости в очередь уведомлений.
/// </summary>
/// <remarks>
/// <para>
/// Лента своя у каждого сервера, настройки уведомлений общие для панели, пауза между
/// повторами своя у сервера. Сервер всегда передаётся явно, а не берётся из контекста запроса:
/// иначе событие фоновой задачи попало бы туда, куда указывал контекст её создания.
/// </para>
/// <para>
/// Ошибка записи в ленту не отменяет действие, о котором сообщает событие.
/// </para>
/// </remarks>
public sealed partial class EventBus
{
	/// <summary>Ёмкость очереди уведомлений, как <c>asyncio.Queue(maxsize=200)</c>.</summary>
	private const int queueCapacity = 200;

	private readonly ConcurrentDictionary<InstanceId, (EventLog Log, NotificationGate Gate)> servers = new();
	private readonly Channel<(InstanceId Instance, JsonObject Record)> queue =
		Channel.CreateBounded<(InstanceId, JsonObject)>(new BoundedChannelOptions(queueCapacity)
		{
			FullMode = BoundedChannelFullMode.DropWrite,
			SingleReader = true,
		});

	private readonly PanelOptions options;
	private readonly TimeProvider clock;
	private readonly ILogger<EventBus> log;

	public EventBus(PanelOptions options, TimeProvider clock, NotificationSettingsFile settings, ILogger<EventBus> log)
	{
		this.options = options;
		this.clock = clock;
		Settings = settings;
		this.log = log;
	}

	public NotificationSettingsFile Settings { get; }

	internal ChannelReader<(InstanceId Instance, JsonObject Record)> Queue => queue.Reader;

	/// <summary>Новое событие сервера. Возвращает запись, как <c>emit()</c>.</summary>
	public async Task<PanelEvent?> EmitAsync(
		InstanceId instance,
		string kind,
		EventSeverity severity,
		Phrase title,
		Phrase? detail = null,
		string source = "panel",
		bool notify = true,
		string? dedupeKey = null,
		CancellationToken ct = default)
	{
		var created = PanelEvent.Create(kind, severity, title, detail, source, clock.GetUtcNow(), dedupeKey);
		if (!created.IsSuccess)
		{
			return null;
		}

		await PublishAsync(instance, created.Value, notify, ct).ConfigureAwait(false);
		return created.Value;
	}

	/// <summary>Готовое событие от наблюдателей сэмплера.</summary>
	public async Task PublishAsync(InstanceId instance, PanelEvent record, bool notify, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(record);

		var server = Server(instance);
		var json = record.ToJson();

		var written = await server.Log.AppendAsync(json, ct).ConfigureAwait(false);
		if (!written.IsSuccess)
		{
			EventLost(log, instance.Value, written.Failure!.Message);
		}

		if (server.Gate.Admit(record, notify, Settings.Current) && !queue.Writer.TryWrite((instance, json)))
		{
			QueueFull(log);
		}
	}

	public Task<IReadOnlyList<JsonObject>> RecentAsync(InstanceId instance, EventFilter filter, int limit, CancellationToken ct) =>
		Server(instance).Log.RecentAsync(filter, limit, clock.GetUtcNow(), ct);

	/// <summary>Запись о неудачной доставке без уведомления, иначе отказ канала порождал бы новые отказы.</summary>
	internal Task RecordDeliveryFailureAsync(InstanceId instance, Phrase detail, CancellationToken ct)
	{
		var record = PanelEvent.Create(
			"notification.failed",
			EventSeverity.Error,
			Phrase.From("Не удалось отправить уведомление"),
			detail,
			"notifications",
			clock.GetUtcNow());

		return Server(instance).Log.AppendAsync(record.Value.ToJson(), ct);
	}

	private (EventLog Log, NotificationGate Gate) Server(InstanceId instance) =>
		servers.GetOrAdd(instance, id => (
			new EventLog(
				Path.Combine(options.InstanceDirectory(id), "events.jsonl"),
				options.EventRetentionDays,
				clock),
			new NotificationGate()));

	[LoggerMessage(Level = LogLevel.Warning, Message = "Событие сервера {Instance} не записано: {Reason}")]
	private static partial void EventLost(ILogger logger, string instance, string reason);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Очередь уведомлений переполнена")]
	private static partial void QueueFull(ILogger logger);
}

/// <summary>
/// Доставка уведомлений из очереди шины.
/// </summary>
public sealed partial class NotificationDispatcher : BackgroundService
{
	private readonly EventBus bus;
	private readonly NotificationSender sender;
	private readonly ILogger<NotificationDispatcher> log;

	public NotificationDispatcher(EventBus bus, NotificationSender sender, ILogger<NotificationDispatcher> log)
	{
		this.bus = bus;
		this.sender = sender;
		this.log = log;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		await foreach (var (instance, record) in bus.Queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
		{
			var settings = bus.Settings.Current;
			var sent = await sender.SendAsync(record, settings, PhraseCatalog.Renderer(PhraseCatalog.Parse(settings.Language)), stoppingToken).ConfigureAwait(false);
			if (!sent.IsSuccess)
			{
				DeliveryFailed(log, sent.Failure!.Message);
				await bus.RecordDeliveryFailureAsync(instance, sent.Failure!.Text, stoppingToken).ConfigureAwait(false);
			}
		}
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "Уведомление не доставлено: {Reason}")]
	private static partial void DeliveryFailed(ILogger logger, string reason);
}
