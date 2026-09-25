using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.Observability.Ports;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Фоновый сбор телеметрии и метрик для каждого сервера реестра.
/// </summary>
/// <remarks>
/// <para>
/// Два цикла на всю панель, и каждый такт опрашивает все серверы параллельно: медленный ответ
/// демона для одного сервера задерживает остальные не больше чем на такт, а сервер, удалённый
/// из реестра, перестаёт опрашиваться.
/// </para>
/// <para>
/// При ошибке чтения реестра такт пропускается, но цикл продолжается: реестр могут править
/// вручную, и на следующем такте он снова прочитается.
/// </para>
/// </remarks>
public sealed partial class MonitoringService : BackgroundService
{
	private readonly IRegistryStore registry;
	private readonly ServerMonitors monitors;
	private readonly ITelemetrySource telemetry;
	private readonly IMetricsSource metrics;
	private readonly PanelOptions options;
	private readonly ILogger<MonitoringService> log;

	public MonitoringService(
		IRegistryStore registry,
		ServerMonitors monitors,
		ITelemetrySource telemetry,
		IMetricsSource metrics,
		PanelOptions options,
		ILogger<MonitoringService> log)
	{
		this.registry = registry;
		this.monitors = monitors;
		this.telemetry = telemetry;
		this.metrics = metrics;
		this.options = options;
		this.log = log;
	}

	protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
		Task.WhenAll(
			LoopAsync(
				TimeSpan.FromSeconds(options.TelemetryIntervalSeconds),
				monitor => monitor.SampleTelemetryAsync(telemetry, stoppingToken),
				stoppingToken),
			LoopAsync(
				TimeSpan.FromSeconds(options.MetricsIntervalSeconds),
				monitor => monitor.SampleMetricsAsync(metrics, stoppingToken),
				stoppingToken));

	private async Task LoopAsync(TimeSpan interval, Func<ServerMonitor, Task> sample, CancellationToken ct)
	{
		using var timer = new PeriodicTimer(interval);

		do
		{
			try
			{
				var servers = await ServersAsync(ct).ConfigureAwait(false);
				var monitors = await Task.WhenAll(servers.Select(this.monitors.GetAsync)).ConfigureAwait(false);
				await Task.WhenAll(monitors.Select(monitor => SafelyAsync(monitor, sample))).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				return;
			}
		}
		while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
	}

	private async Task<IReadOnlyList<InstanceId>> ServersAsync(CancellationToken ct)
	{
		var snapshot = await registry.LoadAsync(ct).ConfigureAwait(false);
		if (snapshot is not RegistrySnapshot.Available available)
		{
			return [];
		}

		var ids = available.Servers.Select(server => server.Id).ToList();
		monitors.Retain(ids);
		return ids;
	}

	private async Task SafelyAsync(ServerMonitor monitor, Func<ServerMonitor, Task> sample)
	{
		try
		{
			await sample(monitor).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			// Ошибка одного сервера не должна останавливать наблюдение за остальными.
			SampleFailed(log, monitor.Instance.Value, exception);
		}
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "Сбор данных сервера {Instance} не удался")]
	private static partial void SampleFailed(ILogger logger, string instance, Exception exception);
}
