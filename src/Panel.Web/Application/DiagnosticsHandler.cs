using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Agent.Client;
using Panel.Contracts;
using Panel.Contracts.Agent;
using Panel.Observability.Domain;
using Panel.Observability.Ports;
using Panel.Provisioning.Ports;
using Panel.Runtime.Ports;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Диагностический пакет одного сервера.
/// </summary>
/// <remarks>
/// <para>
/// Ключи: <c>status</c> — состояние службы без вердикта (он в <c>watchdog</c>), <c>metrics</c> —
/// свежий замер, <c>causes</c> — разбор последней тысячи строк журнала, <c>config_apply</c> —
/// применены ли настройки, <c>recent_events</c> — пятьдесят последних событий панели,
/// <c>panel_journal</c> — хвост журнала служб панели и демона: отказ операции виден только там,
/// <c>agent</c> — сборки панели и демона: разные означают незавершённое обновление.
/// </para>
/// <para>
/// Недоступный журнал или конфигурация не мешают собрать пакет: он нужен как раз тогда, когда
/// что-то сломано. Недоступное состояние службы или замер дают отказ.
/// </para>
/// </remarks>
public sealed class DiagnosticsHandler
{
	private const int journalLines = 1000;
	private const int recentEvents = 50;
	private const int panelJournalLines = 500;

	private readonly GetServerStatusHandler status;
	private readonly IMetricsSource metrics;
	private readonly AutomationService automation;
	private readonly IServerRuntime runtime;
	private readonly ServerConfigurationHandler configuration;
	private readonly UpdateService updates;
	private readonly EventBus events;
	private readonly IProvisioningHost host;
	private readonly AgentClient agent;

	public DiagnosticsHandler(
		GetServerStatusHandler status,
		IMetricsSource metrics,
		AutomationService automation,
		IServerRuntime runtime,
		ServerConfigurationHandler configuration,
		UpdateService updates,
		EventBus events,
		IProvisioningHost host,
		AgentClient agent)
	{
		this.status = status;
		this.metrics = metrics;
		this.automation = automation;
		this.runtime = runtime;
		this.configuration = configuration;
		this.updates = updates;
		this.events = events;
		this.host = host;
		this.agent = agent;
	}

	/// <summary>Пакет уже очищен через <see cref="DiagnosticsRedaction"/>.</summary>
	public async Task<Result<JsonObject>> CollectAsync(InstanceId instance, CancellationToken ct)
	{
		var service = await status.HandleAsync(new GetServerStatus(instance), ct).ConfigureAwait(false);
		if (!service.IsSuccess)
		{
			return service.Failure!;
		}

		var serviceNode = JsonSerializer.SerializeToNode(service.Value, PanelJson.Options)!.AsObject();
		serviceNode.Remove("health");

		var (_, watchdog) = await automation.CurrentHealthAsync(instance).ConfigureAwait(false);

		var journal = await runtime.LogsAsync(instance, journalLines, ct).ConfigureAwait(false);
		IReadOnlyList<LogEvent> lines = journal.IsSuccess
			? [.. journal.Value.Select(entry => LogEvent.FromLine(entry.Line))]
			: [];
		var causes = Causes(service.Value.Active, watchdog, lines, journal.IsSuccess ? null : journal.Failure!.Text);

		var applied = await configuration.ReinspectAsync(instance, ct).ConfigureAwait(false);
		var applyState = applied.IsSuccess
			? JsonSerializer.SerializeToNode(applied.Value, PanelJson.Options)
			: UnknownApplyState(applied.Failure!.Text);

		var sample = await metrics.ReadAsync(instance, ct).ConfigureAwait(false);
		if (!sample.IsSuccess)
		{
			return sample.Failure!;
		}

		var everything = EventFilter.Create("all", string.Empty).Value;
		var recent = await events.RecentAsync(instance, everything, recentEvents, ct).ConfigureAwait(false);

		var payload = new JsonObject
		{
			["status"] = serviceNode,
			["metrics"] = sample.Value.DeepClone(),
			["watchdog"] = watchdog,
			["updates"] = await updates.SnapshotAsync(instance, ct).ConfigureAwait(false),
			["config_apply"] = applyState,
			["causes"] = causes,
			["recent_events"] = new JsonArray([.. recent.Select(item => (JsonNode?) item.DeepClone())]),
			["panel_journal"] = await PanelJournalAsync(ct).ConfigureAwait(false),
			["agent"] = new JsonObject
			{
				["panel_build"] = AgentProtocol.Build,
				["daemon_build"] = agent.DaemonBuild,
				["same_build"] = !agent.BuildsDiffer,
			},
		};

		return DiagnosticsRedaction.Redact(payload)!.AsObject();
	}

	/// <summary>Журнал служб панели; недоступный журнал — причина вместо строк, пакет собирается всё равно.</summary>
	private async Task<JsonObject> PanelJournalAsync(CancellationToken ct)
	{
		var journal = await host.PanelLogsAsync(panelJournalLines, ct).ConfigureAwait(false);
		return journal.IsSuccess
			? new JsonObject
			{
				["unit"] = journal.Value.Unit,
				["lines"] = new JsonArray([.. journal.Value.Lines.Select(line => (JsonNode?) line)]),
			}
			: new JsonObject { ["error"] = PhraseJsonConverter.ToNode(journal.Failure!.Text) };
	}

	/// <summary>
	/// Причины по журналу, плюс <c>journal_error</c>, если журнал не прочитался. «Готов»
	/// определяется вердиктом наблюдателя <c>ready</c>, а не строкой журнала.
	/// </summary>
	public static JsonObject Causes(
		bool active,
		JsonObject watchdog,
		IReadOnlyList<LogEvent> journal,
		Phrase? journalError)
	{
		ArgumentNullException.ThrowIfNull(watchdog);
		ArgumentNullException.ThrowIfNull(journal);

		var ready = Text(watchdog["state"]) == "ready";
		var diagnosis = LogDiagnosis.Diagnose(active, ready, new WatchdogNote(PhraseJsonConverter.FromNode(watchdog["detail"])), journal);

		var causes = new JsonObject
		{
			["startup"] = FindingNode(diagnosis.Startup),
			["connections"] = new JsonArray([.. diagnosis.Connections.Select(finding => (JsonNode?) FindingNode(finding))]),
		};
		if (journalError is not null)
		{
			causes["journal_error"] = PhraseJsonConverter.ToNode(journalError);
		}

		return causes;
	}

	/// <summary>Состояние применения, если конфигурацию не удалось прочитать.</summary>
	private static JsonObject UnknownApplyState(Phrase detail) => new()
	{
		["state"] = "unknown",
		["source"] = "helper",
		["changed_keys"] = new JsonArray(),
		["restart_required"] = false,
		["detail"] = PhraseJsonConverter.ToNode(detail),
	};

	/// <summary>У найденной причины есть строка журнала и её время; у «готов» и «остановлен» их нет.</summary>
	private static JsonObject FindingNode(Finding finding)
	{
		var node = new JsonObject
		{
			["level"] = finding.Level,
			["code"] = finding.Code,
			["title"] = PhraseJsonConverter.ToNode(finding.Title),
			["detail"] = PhraseJsonConverter.ToNode(finding.Detail),
		};
		if (finding.Evidence is not null)
		{
			node["evidence"] = finding.Evidence;
			node["timestamp"] = finding.Timestamp;
		}

		return node;
	}

	private static string? Text(JsonNode? value) =>
		value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
}
