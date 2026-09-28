using System.Text.Json.Nodes;
using Panel.SharedKernel;

namespace Panel.Maintenance.Domain;

/// <summary>Итог прогона обслуживания.</summary>
public enum RunStatus
{
	Queued,
	Running,
	Success,
	Failed,
	Cancelled,
	Skipped,
}

public static class RunStatusWire
{
	public static string ToWire(RunStatus status) => status switch
	{
		RunStatus.Queued => "queued",
		RunStatus.Running => "running",
		RunStatus.Success => "success",
		RunStatus.Failed => "failed",
		RunStatus.Cancelled => "cancelled",
		_ => "skipped",
	};
}

/// <summary>Шаг прогона: <c>{action, status, …}</c> в <c>automation-runs.jsonl</c>.</summary>
public sealed record RunStep(string Action, string Status, Phrase? Detail = null, int? Minutes = null, string? BackupName = null)
{
	public JsonObject ToJson()
	{
		var step = new JsonObject { ["action"] = Action, ["status"] = Status };
		if (Minutes is { } minutes)
		{
			step["minutes"] = minutes;
		}

		if (BackupName is not null)
		{
			step["name"] = BackupName;
		}

		if (Detail is not null)
		{
			step["detail"] = PhraseJsonConverter.ToNode(Detail);
		}

		return step;
	}
}

/// <summary>
/// Прогон планового обслуживания или восстановления наблюдателем: строка
/// <c>automation-runs.jsonl</c> и поле <c>running</c> ответа <c>/api/automation</c>.
/// </summary>
/// <remarks>
/// Шаги только добавляются, итог выставляется один раз. Прогон, прерванный остановкой
/// панели, записывается как <c>cancelled</c> с объяснением.
/// </remarks>
public sealed class MaintenanceRun
{
	private readonly List<RunStep> steps = [];

	public MaintenanceRun(string id, string? jobId, string name, string trigger, long startedAt)
	{
		Id = id;
		JobId = jobId;
		Name = name;
		Trigger = trigger;
		StartedAt = startedAt;
	}

	public string Id { get; }

	public string? JobId { get; }

	public string Name { get; }

	public string Trigger { get; }

	public long StartedAt { get; }

	public long FinishedAt { get; private set; }

	public RunStatus Status { get; private set; } = RunStatus.Queued;

	public Phrase? Detail { get; private set; }

	public IReadOnlyList<RunStep> Steps => steps;

	public void Begin() => Status = RunStatus.Running;

	public void Step(RunStep step)
	{
		ArgumentNullException.ThrowIfNull(step);
		steps.Add(step);
	}

	/// <summary>Все шаги выполнены.</summary>
	public void Succeed(long finishedAt) => Finish(RunStatus.Success, Phrase.From("Обслуживание завершено"), finishedAt);

	/// <summary>Панель остановилась посреди прогона.</summary>
	public void Cancel(long finishedAt) => Finish(RunStatus.Cancelled, Phrase.From("Выполнение остановлено вместе с панелью"), finishedAt);

	/// <summary>Срок пропущен больше чем на шесть часов: панель была выключена.</summary>
	public void SkipMissed(long finishedAt) => Finish(RunStatus.Skipped, Phrase.From("Пропущено: панель была выключена более 6 часов"), finishedAt);

	/// <summary>Наблюдатель перезапустил зависший сервер.</summary>
	public void Recovered(long finishedAt) => Finish(RunStatus.Success, Phrase.From("Автоматический перезапуск зависшего сервера"), finishedAt);

	/// <summary>Шаг не удался; его причина становится итогом прогона.</summary>
	public void Fail(Failure reason, long finishedAt)
	{
		ArgumentNullException.ThrowIfNull(reason);
		Finish(RunStatus.Failed, reason.Text, finishedAt);
	}

	private void Finish(RunStatus status, Phrase detail, long finishedAt)
	{
		if (Status is RunStatus.Success or RunStatus.Failed or RunStatus.Cancelled or RunStatus.Skipped)
		{
			throw new InvalidOperationException("Прогон уже завершён");
		}

		Status = status;
		Detail = detail;
		FinishedAt = finishedAt;
	}

	public JsonObject ToJson()
	{
		var record = new JsonObject
		{
			["id"] = Id,
			["job_id"] = JobId,
			["name"] = Name,
			["trigger"] = Trigger,
			["started_at"] = StartedAt,
			["finished_at"] = FinishedAt,
			["status"] = RunStatusWire.ToWire(Status),
			["steps"] = new JsonArray([.. steps.Select(step => (JsonNode?) step.ToJson())]),
		};

		if (Detail is not null)
		{
			record["detail"] = PhraseJsonConverter.ToNode(Detail);
		}

		return record;
	}
}
