using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Panel.Observability.Domain;
using Panel.Observability.Infrastructure;
using Panel.Observability.Ports;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Наблюдение за одним сервером: история телеметрии и метрик в памяти и на диске.
/// </summary>
/// <remarks>
/// <para>
/// Сервер передаётся явно: монитор создаётся для идентификатора и не зависит от контекста
/// запроса.
/// </para>
/// <para>
/// Историю читают и пополняют разные потоки (сэмплер и маршруты), поэтому доменные объекты
/// используются только под <see cref="sync"/>. Ввод-вывод идёт вне этой блокировки, чтобы
/// чтение графика не ждало записи на диск.
/// </para>
/// </remarks>
public sealed class ServerMonitor
{
	/// <summary>Как часто уплотняется история телеметрии.</summary>
	private static readonly TimeSpan telemetryCompaction = TimeSpan.FromHours(1);

	/// <summary>Как часто уплотняется история метрик.</summary>
	private static readonly TimeSpan metricsCompaction = TimeSpan.FromHours(6);

	private readonly object sync = new();
	private readonly SemaphoreSlim metricsGate = new(1, 1);
	private readonly TelemetryHistory telemetry;
	private readonly MetricsHistory metrics;
	private readonly HistoryFile telemetryFile;
	private readonly HistoryFile metricsFile;
	private readonly TimeProvider clock;
	private readonly EventBus events;
	private readonly ServerWatch serverWatch = new();
	private readonly TelemetryWatch telemetryWatch = new();
	private DateTimeOffset telemetryCompacted;
	private DateTimeOffset metricsCompacted;

	private ServerMonitor(InstanceId instance, PanelOptions options, TimeProvider clock, EventBus events)
	{
		Instance = instance;
		this.clock = clock;
		this.events = events;
		telemetry = new TelemetryHistory(TimeSpan.FromHours(options.TelemetryHistoryHours));
		metrics = new MetricsHistory(TimeSpan.FromDays(options.MetricsHistoryDays));
		telemetryFile = new HistoryFile(Path.Combine(options.InstanceDirectory(instance), "telemetry.jsonl"));
		metricsFile = new HistoryFile(Path.Combine(options.InstanceDirectory(instance), "metrics.jsonl"));
		telemetryCompacted = clock.GetUtcNow();
		metricsCompacted = clock.GetUtcNow();
		TelemetryInterval = TimeSpan.FromSeconds(options.TelemetryIntervalSeconds);
		MetricsInterval = TimeSpan.FromSeconds(options.MetricsIntervalSeconds);
	}

	public InstanceId Instance { get; }

	public TimeSpan TelemetryInterval { get; }

	public TimeSpan MetricsInterval { get; }

	/// <summary>Монитор с историей, загруженной из файлов.</summary>
	public static async Task<ServerMonitor> OpenAsync(
		InstanceId instance,
		PanelOptions options,
		TimeProvider clock,
		EventBus events,
		CancellationToken ct)
	{
		var monitor = new ServerMonitor(instance, options, clock, events);
		var telemetry = await monitor.telemetryFile.LoadAsync(ct).ConfigureAwait(false);
		var metrics = await monitor.metricsFile.LoadAsync(ct).ConfigureAwait(false);
		var now = clock.GetUtcNow();

		lock (monitor.sync)
		{
			monitor.telemetry.Restore(telemetry, now);
			monitor.metrics.Restore(metrics, now);
		}

		return monitor;
	}

	public async Task SampleTelemetryAsync(ITelemetrySource source, CancellationToken ct)
	{
		var snapshot = await source.ReadAsync(Instance, ct).ConfigureAwait(false);
		var now = clock.GetUtcNow();

		JsonObject stored;
		IReadOnlyList<PanelEvent> events;
		lock (sync)
		{
			stored = telemetry.Record(snapshot, now);
			events = telemetryWatch.Observe(snapshot, now);
		}

		await telemetryFile.AppendAsync(stored, ct).ConfigureAwait(false);
		await PublishAsync(events, ct).ConfigureAwait(false);

		if (now - telemetryCompacted >= telemetryCompaction)
		{
			telemetryCompacted = now;
			var cutoff = now.ToUnixTimeSeconds() - (long) telemetry.MaxAge.TotalSeconds;
			await telemetryFile.PruneAsync("sampled_at", cutoff, ct).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Замер метрик. Если демон вернул отказ, замер пропускается: точка без данных испортила бы
	/// график загрузки процессора.
	/// </summary>
	public async Task SampleMetricsAsync(IMetricsSource source, CancellationToken ct)
	{
		await metricsGate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			var current = await source.ReadAsync(Instance, ct).ConfigureAwait(false);
			if (!current.IsSuccess)
			{
				return;
			}

			var now = clock.GetUtcNow();
			MetricsPoint point;
			IReadOnlyList<MetricsPoint>? retained = null;
			IReadOnlyList<PanelEvent> events;

			lock (sync)
			{
				point = metrics.Record(current.Value, now);
				events = serverWatch.Observe(current.Value, now);
				if (now - metricsCompacted >= metricsCompaction)
				{
					metricsCompacted = now;
					retained = [.. metrics.Points];
				}
			}

			await metricsFile.AppendAsync(point.ToJson(), ct).ConfigureAwait(false);
			await PublishAsync(events, ct).ConfigureAwait(false);

			if (retained is not null)
			{
				await metricsFile.RewriteAsync(retained.Select(item => item.ToJson()), ct).ConfigureAwait(false);
			}
		}
		finally
		{
			metricsGate.Release();
		}
	}

	/// <summary>
	/// События наблюдателей в ленту сервера. Уведомления отправляются для всех, кроме входов и
	/// выходов: у них важность <c>info</c>, и они пройдут, только если порог понижен до info.
	/// </summary>
	private async Task PublishAsync(IReadOnlyList<PanelEvent> events, CancellationToken ct)
	{
		foreach (var item in events)
		{
			await this.events.PublishAsync(Instance, item, notify: true, ct).ConfigureAwait(false);
		}
	}

	/// <summary>Текущий снимок с оценкой соединения игроков.</summary>
	public JsonObject CurrentTelemetry()
	{
		lock (sync)
		{
			return telemetry.Current(clock.GetUtcNow());
		}
	}

	public IReadOnlyList<PingSample> PlayerHistory(TelemetryPlayer player, TimeSpan window)
	{
		lock (sync)
		{
			return telemetry.PlayerHistory(player, clock.GetUtcNow(), window);
		}
	}

	public PerformanceReport Performance(TimeSpan window)
	{
		lock (sync)
		{
			return telemetry.Performance(clock.GetUtcNow(), window);
		}
	}

	public (JsonObject? Latest, IReadOnlyList<MetricsPoint> Points) Metrics(TimeSpan window)
	{
		lock (sync)
		{
			return ((JsonObject?) metrics.Latest?.DeepClone(), metrics.Window(clock.GetUtcNow(), window));
		}
	}

	public bool HasMetrics
	{
		get
		{
			lock (sync)
			{
				return metrics.Latest is not null;
			}
		}
	}
}

/// <summary>
/// Мониторы всех серверов реестра: для нового сервера монитор создаётся, для удалённого
/// отпускается.
/// </summary>
public sealed class ServerMonitors
{
	private readonly ConcurrentDictionary<InstanceId, Lazy<Task<ServerMonitor>>> monitors = new();
	private readonly PanelOptions options;
	private readonly TimeProvider clock;
	private readonly EventBus events;

	public ServerMonitors(PanelOptions options, TimeProvider clock, EventBus events)
	{
		this.options = options;
		this.clock = clock;
		this.events = events;
	}

	public Task<ServerMonitor> GetAsync(InstanceId instance) =>
		monitors.GetOrAdd(
			instance,
			id => new Lazy<Task<ServerMonitor>>(() => ServerMonitor.OpenAsync(id, options, clock, events, CancellationToken.None)))
			.Value;

	/// <summary>Отпустить монитор удалённого сервера, не дожидаясь такта сэмплера.</summary>
	public void Release(InstanceId instance) => monitors.TryRemove(instance, out _);

	/// <summary>Отпустить мониторы серверов, которых больше нет в реестре.</summary>
	public void Retain(IReadOnlyCollection<InstanceId> present)
	{
		foreach (var instance in monitors.Keys)
		{
			if (!present.Contains(instance))
			{
				monitors.TryRemove(instance, out _);
			}
		}
	}
}
