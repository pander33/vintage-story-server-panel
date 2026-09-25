using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace Panel.Agent.Supervision;

/// <summary>
/// Демон как служба Windows: отвечает диспетчеру служб на запуск и остановку.
/// </summary>
/// <remarks>
/// Остановка службы останавливает только демон. Игровые серверы — его дочерние процессы,
/// но Windows не завершает их вместе с родителем: они продолжают работать, и после запуска
/// службы демон находит их снова.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class AgentWindowsService : ServiceBase
{
	/// <summary>Сколько ждать, пока канал закроется после сигнала остановки.</summary>
	private static readonly TimeSpan stopWait = TimeSpan.FromSeconds(20);

	private readonly Func<CancellationToken, Task> run;
	private CancellationTokenSource? stopping;
	private Task? running;

	public AgentWindowsService(Func<CancellationToken, Task> run)
	{
		ArgumentNullException.ThrowIfNull(run);
		this.run = run;
		ServiceName = "vs-panel-agent";
		CanStop = true;
		CanShutdown = true;
		AutoLog = true;
	}

	protected override void OnStart(string[] args)
	{
		stopping = new CancellationTokenSource();
		running = Task.Run(() => run(stopping.Token));

		// Если демон упал сам, служба не должна оставаться запущенной без канала.
		running.ContinueWith(
			task =>
			{
				EventLog.WriteEntry($"vs-panel-agent остановился с ошибкой: {task.Exception?.GetBaseException().Message}", EventLogEntryType.Error);
				ExitCode = 1;
				Stop();
			},
			CancellationToken.None,
			TaskContinuationOptions.OnlyOnFaulted,
			TaskScheduler.Default);
	}

	protected override void OnStop() => Shutdown();

	protected override void OnShutdown() => Shutdown();

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			stopping?.Dispose();
		}

		base.Dispose(disposing);
	}

	private void Shutdown()
	{
		stopping?.Cancel();
		try
		{
			running?.Wait(stopWait);
		}
		catch (AggregateException)
		{
			// Отмена и падение уже записаны; служба всё равно останавливается.
		}
	}
}
