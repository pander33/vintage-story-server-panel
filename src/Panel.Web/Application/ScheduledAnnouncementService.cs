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

		var written = await workspace.RecordScheduleAsync(() =>
		{
			for (var index = 0; index < workspace.Announcements.Count; index++)
			{
				// Объявление могли изменить, пока шла отправка: итог записывается в текущую версию.
				if (outcomes.TryGetValue(workspace.Announcements[index].Id, out var outcome))
				{
					workspace.Announcements[index] = workspace.Announcements[index].Advance(now, outcome);
				}
			}

			return true;
		}, ct).ConfigureAwait(false);

		if (!written.IsSuccess)
		{
			throw new IOException(written.Failure!.Message);
		}
	}

	public async Task<Result<JsonObject>> AddAnnouncementAsync(InstanceId instance, AnnouncementDraft draft, CancellationToken ct)
	{
		var created = ScheduledAnnouncement.Create(draft, null, clock.GetUtcNow());
		if (!created.IsSuccess)
		{
			return Result.Fail<JsonObject>(created.Failure!);
		}

		var workspace = await workspaces.GetAsync(instance).ConfigureAwait(false);
		return await workspace.ChangeAsync<JsonObject>(
			() =>
			{
				if (workspace.Announcements.Count >= maxAnnouncements)
				{
					return Failure.Invalid($"Не больше {maxAnnouncements} объявлений на сервер");
				}

				workspace.Announcements.Add(created.Value);
				return new WorkspaceChange<JsonObject>(
					created.Value.ToJson(),
					() => workspace.Announcements.RemoveAll(item => item.Id == created.Value.Id));
			},
			ct).ConfigureAwait(false);
	}

	/// <summary>Изменение и переключение: присланное накладывается на объявление.</summary>
	public async Task<Result<JsonObject>> UpdateAnnouncementAsync(
		InstanceId instance,
		string id,
		Func<AnnouncementDraft, AnnouncementDraft> change,
		CancellationToken ct)
	{
		var workspace = await workspaces.GetAsync(instance).ConfigureAwait(false);
		return await workspace.ChangeAsync<JsonObject>(
			() =>
			{
				var index = workspace.Announcements.FindIndex(item => item.Id == id);
				if (index < 0)
				{
					return Failure.NotFound("Объявление не найдено");
				}

				var current = workspace.Announcements[index];
				var updated = ScheduledAnnouncement.Create(change(current.ToDraft()), id, clock.GetUtcNow(), current);
				if (!updated.IsSuccess)
				{
					return updated.Failure!;
				}

				workspace.Announcements[index] = updated.Value;
				return new WorkspaceChange<JsonObject>(
					updated.Value.ToJson(),
					() => AutomationService.Replace(workspace.Announcements, item => item.Id == id, current));
			},
			ct).ConfigureAwait(false);
	}

	public async Task<Result<Done>> DeleteAnnouncementAsync(InstanceId instance, string id, CancellationToken ct)
	{
		var workspace = await workspaces.GetAsync(instance).ConfigureAwait(false);
		return await workspace.ChangeAsync<Done>(
			() =>
			{
				var index = workspace.Announcements.FindIndex(item => item.Id == id);
				if (index < 0)
				{
					return Failure.NotFound("Объявление не найдено");
				}

				var removed = workspace.Announcements[index];
				workspace.Announcements.RemoveAt(index);
				return new WorkspaceChange<Done>(
					Done.Value,
					() => workspace.Announcements.Insert(Math.Min(index, workspace.Announcements.Count), removed));
			},
			ct).ConfigureAwait(false);
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "Объявление по расписанию на сервере {Instance} не отправлено: {Reason}")]
	private static partial void AnnouncementFailed(ILogger logger, string instance, string reason);
}
