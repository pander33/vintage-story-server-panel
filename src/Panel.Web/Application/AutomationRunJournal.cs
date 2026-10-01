using Panel.Maintenance.Domain;
using Panel.Observability.Domain;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>История прогонов и их события.</summary>
internal sealed class AutomationRunJournal(EventBus events)
{
	internal async Task RecordAsync(InstanceId instance, AutomationWorkspace workspace, MaintenanceRun run, bool withTrigger, CancellationToken ct)
	{
		var record = run.ToJson();
		if (!withTrigger)
		{
			record.Remove("trigger");
			record.Remove("steps");
		}

		await workspace.AppendRunAsync(record, ct).ConfigureAwait(false);

		var status = RunStatusWire.ToWire(run.Status);
		await events.EmitAsync(
				instance,
				"automation." + status,
				run.Status is RunStatus.Success or RunStatus.Skipped ? EventSeverity.Info : EventSeverity.Error,
				Phrase.Format($"Обслуживание: {run.Name}"),
				run.Detail,
				"automation",
				ct: ct)
			.ConfigureAwait(false);
	}
}
