using System.Collections.Concurrent;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>Однократное открытие состояния сервера и восстановление последнего вердикта.</summary>
internal sealed class AutomationWorkspaceStore(PanelOptions options, ReadinessLog readiness, TimeProvider clock, TimeZoneInfo zone)
{
	private readonly ConcurrentDictionary<InstanceId, Lazy<Task<AutomationWorkspace>>> workspaces = new();

	internal Task<AutomationWorkspace> GetAsync(InstanceId instance) =>
		workspaces.GetOrAdd(instance, id => new Lazy<Task<AutomationWorkspace>>(() => OpenAsync(id))).Value;

	/// <summary>
	/// Состояние сервера из файла. Вердикт и настройки сразу видны в <c>/api/status</c>, а
	/// сохранённая цепочка продолжается после перезапуска панели.
	/// </summary>
	private async Task<AutomationWorkspace> OpenAsync(InstanceId instance)
	{
		var workspace = await AutomationWorkspace.OpenAsync(
				Path.Combine(options.InstanceDirectory(instance), "automation.json"),
				Path.Combine(options.InstanceDirectory(instance), "automation-runs.jsonl"),
				clock,
				zone)
			.ConfigureAwait(false);

		readiness.Configure(instance, workspace.Watchdog);
		readiness.Record(instance, workspace.Chain, WatchdogService.RecoveryBlocked(workspace.Health));
		return workspace;
	}
}
