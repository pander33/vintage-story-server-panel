using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Panel.Maintenance.Domain;
using Panel.Observability.Domain;
using Panel.Runtime.Domain;
using Panel.Runtime.Ports;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>Расписание объявлений, его редактирование и отправка сообщений игрокам.</summary>
internal sealed partial class ScheduledAnnouncementService(
	AutomationWorkspaceStore workspaces, ITelemetryReader telemetry, Announcements announcements,
	TimeProvider clock, ILogger<AutomationService> log)
{
	private const int maxAnnouncements = 20;

	/// <summary>
	/// Объявления, срок которых наступил. Остановленному серверу и пустому (если так задано)
	/// объявление не отправляется, но срок всё равно переходит к следующему: копить пропущенные
	/// незачем.
	/// </summary>
	internal async Task AnnounceDueAsync(InstanceId instance, AutomationWorkspace workspace, CancellationToken ct)
	{
		var now = clock.GetUtcNow();
		List<ScheduledAnnouncement> due;
		lock (workspace.Sync)
		{
			due = [.. workspace.Announcements.Where(item => item.IsDue(now))];
		}

		if (due.Count == 0)
		{
			return;
		}

		// Вердикт только что записал такт наблюдателя; без него сервер считается остановленным.
		bool active;
		lock (workspace.Sync)
		{
			active = TelemetryNumbers.Truthy(workspace.Health["active"]);
		}
		int? online = null;
		if (active && due.Any(item => item.PlayersOnly)
			&& await telemetry.ReadAsync(instance, ct).ConfigureAwait(false) is TelemetryReading.Fresh fresh)
		{
			// Без свежей телеметрии число игроков неизвестно, и объявление уходит: лучше лишнее
			// сообщение, чем молчание на сервере без мода.
			online = fresh.Snapshot.Players.Count;
		}

		var outcomes = new Dictionary<string, AnnouncementOutcome>(StringComparer.Ordinal);
		foreach (var item in due)
		{
			if (!active || (item.PlayersOnly && online == 0))
			{
				outcomes[item.Id] = AnnouncementOutcome.Skipped;
				continue;
			}

			var sent = await announcements.SendTextAsync(instance, item.Text, ct).ConfigureAwait(false);
			if (!sent.IsSuccess)
			{
				AnnouncementFailed(log, instance.Value, sent.Failure!.Message);
			}

			outcomes[item.Id] = sent.IsSuccess ? AnnouncementOutcome.Sent : AnnouncementOutcome.Failed;
		}

		lock (workspace.Sync)
		{
			for (var index = 0; index < workspace.Announcements.Count; index++)
			{
				// Объявление могли изменить, пока шла отправка: итог записывается в текущую версию.
				if (outcomes.TryGetValue(workspace.Announcements[index].Id, out var outcome))
				{
					workspace.Announcements[index] = workspace.Announcements[index].Advance(now, outcome);
				}
			}
		}

		await workspace.SaveRequiredAsync(ct).ConfigureAwait(false);
	}

	public async Task<Result<JsonObject>> AddAnnouncementAsync(InstanceId instance, AnnouncementDraft draft, CancellationToken ct)
	{
		var created = ScheduledAnnouncement.Create(draft, null, clock.GetUtcNow());
		if (!created.IsSuccess)
		{
			return Result.Fail<JsonObject>(created.Failure!);
		}

		var workspace = await workspaces.GetAsync(instance).ConfigureAwait(false);
		lock (workspace.Sync)
		{
			if (workspace.Announcements.Count >= maxAnnouncements)
			{
				return Failure.Invalid($"Не больше {maxAnnouncements} объявлений на сервер");
			}

			workspace.Announcements.Add(created.Value);
		}

		var written = await workspace.SaveAsync(ct).ConfigureAwait(false);
		return written.IsSuccess ? created.Value.ToJson() : written.Failure!;
	}

	/// <summary>Изменение и переключение: присланное накладывается на объявление.</summary>
	public async Task<Result<JsonObject>> UpdateAnnouncementAsync(
		InstanceId instance,
		string id,
		Func<AnnouncementDraft, AnnouncementDraft> change,
		CancellationToken ct)
	{
		var workspace = await workspaces.GetAsync(instance).ConfigureAwait(false);
		Result<ScheduledAnnouncement> updated;

		lock (workspace.Sync)
		{
			var index = workspace.Announcements.FindIndex(item => item.Id == id);
			if (index < 0)
			{
				return Failure.NotFound("Объявление не найдено");
			}

			var current = workspace.Announcements[index];
			updated = ScheduledAnnouncement.Create(change(current.ToDraft()), id, clock.GetUtcNow(), current);
			if (updated.IsSuccess)
			{
				workspace.Announcements[index] = updated.Value;
			}
		}

		if (!updated.IsSuccess)
		{
			return Result.Fail<JsonObject>(updated.Failure!);
		}

		var written = await workspace.SaveAsync(ct).ConfigureAwait(false);
		return written.IsSuccess ? updated.Value.ToJson() : written.Failure!;
	}

	public async Task<Result<Done>> DeleteAnnouncementAsync(InstanceId instance, string id, CancellationToken ct)
	{
		var workspace = await workspaces.GetAsync(instance).ConfigureAwait(false);
		lock (workspace.Sync)
		{
			if (workspace.Announcements.RemoveAll(item => item.Id == id) == 0)
			{
				return Failure.NotFound("Объявление не найдено");
			}
		}

		return await workspace.SaveAsync(ct).ConfigureAwait(false);
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "Объявление по расписанию на сервере {Instance} не отправлено: {Reason}")]
	private static partial void AnnouncementFailed(ILogger logger, string instance, string reason);
}
