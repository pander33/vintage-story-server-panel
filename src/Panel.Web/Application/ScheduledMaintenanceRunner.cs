using Microsoft.Extensions.Logging;
using Panel.Content.Ports;
using Panel.Fleet.Domain;
using Panel.Maintenance.Domain;
using Panel.Runtime.Domain;
using Panel.Runtime.Ports;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>Исполнение одного задания; аренда уже захвачена планировщиком.</summary>
internal sealed partial class ScheduledMaintenanceRunner(
	IServerRuntime runtime, ServerControlHandler control, Announcements announcements,
	IBackupStore backups, IReadinessWaiter readiness, AutomationRunJournal journal, TimeProvider clock, ILogger<AutomationService> log)
{
	/// <summary>
	/// Прогон: предупреждения игрокам, остановка, копия, запуск, ожидание готовности, либо только
	/// копия.
	/// </summary>
	internal async Task RunAsync(
		InstanceId instance,
		AutomationWorkspace workspace,
		MaintenanceJob job,
		MaintenanceRun run,
		MaintenanceCoordinator.Lease lease, CancellationToken ct)
	{
		using (lease)
		{
			run.Begin();
			try
			{
				var status = await runtime.StatusAsync(instance, ct).ConfigureAwait(false);
				var active = status.IsSuccess && status.Value.IsActive;
				var warnings = active ? job.WarningMinutes : [];

				if (job.WarningMinutes.Count > 0 && !active)
				{
					run.Step(new RunStep("countdown", "skipped", Phrase.From("Сервер уже остановлен")));
				}

				var previous = warnings.Count > 0 ? warnings[0] : 0;
				for (var index = 0; index < warnings.Count; index++)
				{
					var minutes = warnings[index];
					if (index > 0)
					{
						await Task.Delay(TimeSpan.FromMinutes(previous - minutes), ct).ConfigureAwait(false);
					}

					var sent = await announcements.SendAsync(instance, Phrase.Format($"Плановое обслуживание через {minutes} мин."), ct).ConfigureAwait(false);
					run.Step(sent.IsSuccess
						? new RunStep("announce", "ok", Minutes: minutes)
						: new RunStep("announce", "warning", sent.Failure!.Text));
					previous = minutes;
				}

				if (warnings.Count > 0 && previous > 0)
				{
					await Task.Delay(TimeSpan.FromMinutes(previous), ct).ConfigureAwait(false);
				}

				if (job.Restart)
				{
					Require(await control.ServiceAsync(instance, ServiceAction.Stop, ct).ConfigureAwait(false));
					run.Step(new RunStep("stop", "ok"));

					try
					{
						if (job.Backup)
						{
							var backup = Require(await backups.CreateAsync(instance, offline: true, ct).ConfigureAwait(false));
							run.Step(new RunStep("backup", "ok", BackupName: backup.Name));
						}
					}
					finally
					{
						// После неудачной копии сервер не должен остаться остановленным.
						Require(await control.ServiceAsync(instance, ServiceAction.Start, CancellationToken.None).ConfigureAwait(false));
					}

					run.Step(new RunStep("start", "ok"));

					if (!await readiness.WaitReadyAsync(instance, TimeSpan.FromSeconds(job.ReadinessTimeoutSeconds), ct).ConfigureAwait(false))
					{
						throw new MaintenanceFailed(Failure.Unavailable("Сервер не перешёл в состояние «Готов» за отведённое время"));
					}

					run.Step(new RunStep("wait_ready", "ok"));
				}
				else if (job.Backup)
				{
					var backup = Require(await backups.CreateAsync(instance, offline: false, ct).ConfigureAwait(false));
					run.Step(new RunStep("backup", "ok", BackupName: backup.Name));
				}

				run.Succeed(Now());
			}
			catch (OperationCanceledException)
			{
				run.Cancel(Now());
			}
			catch (MaintenanceFailed failure)
			{
				run.Fail(failure.Reason, Now());
			}
			catch (Exception exception) when (exception is not OperationCanceledException)
			{
				// Любой отказ означает неудачный прогон: задача запущена без наблюдения, и исключение иначе
				// нигде бы не записалось.
				run.Fail(Failure.Unavailable(exception.Message), Now());
			}
			finally
			{
				var written = await workspace.RecordScheduleAsync(() =>
				{
					var index = workspace.Jobs.FindIndex(item => item.Id == job.Id);
					if (index >= 0)
					{
						workspace.Jobs[index] = workspace.Jobs[index].WithResult(run.StartedAt, run.Status);
					}

					workspace.Running = null;
					return true;
				}, CancellationToken.None).ConfigureAwait(false);

				await journal.RecordAsync(instance, workspace, run, withTrigger: true, CancellationToken.None).ConfigureAwait(false);
				if (!written.IsSuccess)
				{
					StateSaveFailed(log, instance.Value, written.Failure!.Message);
				}
			}
		}
	}

	private long Now() => clock.GetUtcNow().ToUnixTimeSeconds();

	private static T Require<T>(Result<T> result) =>
		result.IsSuccess ? result.Value : throw new MaintenanceFailed(result.Failure!);

	/// <summary>Отказ шага, который прерывает прогон; его причина становится итогом.</summary>
	private sealed class MaintenanceFailed(Failure reason) : Exception(reason.Message)
	{
		public Failure Reason { get; } = reason;
	}

	[LoggerMessage(Level = LogLevel.Error, Message = "Состояние обслуживания сервера {Instance} не сохранено: {Reason}")]
	private static partial void StateSaveFailed(ILogger logger, string instance, string reason);

}
