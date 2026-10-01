using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Contracts.ServerCommands;
using Panel.SharedKernel;

namespace Panel.Maintenance.Domain;

/// <summary>Чем кончилась последняя попытка объявления.</summary>
public enum AnnouncementOutcome
{
	Sent,

	/// <summary>Сервер остановлен или на нём никого нет: объявлять некому.</summary>
	Skipped,

	Failed,
}

public static class AnnouncementOutcomeWire
{
	public static string ToWire(AnnouncementOutcome outcome) => outcome switch
	{
		AnnouncementOutcome.Sent => "sent",
		AnnouncementOutcome.Skipped => "skipped",
		_ => "failed",
	};
}

/// <summary>Данные формы объявления с умолчаниями формы.</summary>
public sealed record AnnouncementDraft
{
	public string Text { get; init; } = string.Empty;
	public bool Enabled { get; init; } = true;
	public int IntervalMinutes { get; init; } = 60;
	public bool PlayersOnly { get; init; } = true;
}

/// <summary>
/// Объявление по расписанию: один и тот же текст игрокам раз в N минут (правила, адрес Discord,
/// напоминание о вайпе). Элемент <c>announcements</c> в <c>automation.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// В отличие от <see cref="MaintenanceJob"/>, объявление не берёт аренду обслуживания, не пишет
/// прогон в историю и не шлёт событие: раз в пять минут это засорило бы ленту и уведомления.
/// Итог виден в самом объявлении (<see cref="LastResult"/>).
/// </para>
/// <para>
/// Пропущенные сроки не догоняются: после простоя панели объявление уходит один раз, и
/// следующий срок отсчитывается от этого момента.
/// </para>
/// </remarks>
public sealed record ScheduledAnnouncement
{
	public const int MinIntervalMinutes = 5;
	public const int MaxIntervalMinutes = 1440;

	public required string Id { get; init; }

	/// <summary>Текст как его пишет администратор; на язык сервера не переводится.</summary>
	public required string Text { get; init; }

	public required bool Enabled { get; init; }
	public required int IntervalMinutes { get; init; }

	/// <summary>Не объявлять на пустой сервер. Без телеметрии число игроков неизвестно, и объявление уходит.</summary>
	public required bool PlayersOnly { get; init; }

	public required long CreatedAt { get; init; }
	public required long UpdatedAt { get; init; }
	public long LastSentAt { get; init; }
	public string? LastResult { get; init; }
	public long? NextRun { get; init; }

	public static Result<ScheduledAnnouncement> Create(
		AnnouncementDraft draft,
		string? id,
		DateTimeOffset now,
		ScheduledAnnouncement? previous = null)
	{
		ArgumentNullException.ThrowIfNull(draft);

		// Та же проверка, что у объявления вручную: длина и запрет управляющих символов.
		var command = PlayerCommand.Create("announce", string.Empty, (draft.Text ?? string.Empty).Trim());
		if (!command.IsSuccess)
		{
			return Result.Fail<ScheduledAnnouncement>(command.Failure!);
		}

		if (draft.IntervalMinutes is < MinIntervalMinutes or > MaxIntervalMinutes)
		{
			return Failure.Invalid($"Интервал объявления должен быть от {MinIntervalMinutes} до {MaxIntervalMinutes} минут");
		}

		// Срок сохраняется, если не изменились ни интервал, ни включённость: правка текста не
		// откладывает очередное объявление.
		var keepSchedule = previous is { Enabled: true, NextRun: not null }
			&& draft.Enabled
			&& previous.IntervalMinutes == draft.IntervalMinutes;

		return new ScheduledAnnouncement
		{
			Id = id ?? Guid.NewGuid().ToString("N"),
			Text = command.Value.Detail,
			Enabled = draft.Enabled,
			IntervalMinutes = draft.IntervalMinutes,
			PlayersOnly = draft.PlayersOnly,
			CreatedAt = previous?.CreatedAt is > 0 and var created ? created : now.ToUnixTimeSeconds(),
			UpdatedAt = now.ToUnixTimeSeconds(),
			LastSentAt = previous?.LastSentAt ?? 0,
			LastResult = previous?.LastResult,
			NextRun = !draft.Enabled ? null
				: keepSchedule ? previous!.NextRun
				: now.ToUnixTimeSeconds() + (draft.IntervalMinutes * 60L),
		};
	}

	public AnnouncementDraft ToDraft() => new()
	{
		Text = Text,
		Enabled = Enabled,
		IntervalMinutes = IntervalMinutes,
		PlayersOnly = PlayersOnly,
	};

	public bool IsDue(DateTimeOffset now) => Enabled && NextRun is { } next && next <= now.ToUnixTimeSeconds();

	/// <summary>Попытка состоялась: итог записывается, следующий срок — через интервал от сейчас.</summary>
	public ScheduledAnnouncement Advance(DateTimeOffset now, AnnouncementOutcome outcome) => this with
	{
		LastSentAt = outcome == AnnouncementOutcome.Sent ? now.ToUnixTimeSeconds() : LastSentAt,
		LastResult = AnnouncementOutcomeWire.ToWire(outcome),
		NextRun = Enabled ? now.ToUnixTimeSeconds() + (IntervalMinutes * 60L) : null,
	};

	public JsonObject ToJson() => new()
	{
		["id"] = Id,
		["text"] = Text,
		["enabled"] = Enabled,
		["interval_minutes"] = IntervalMinutes,
		["players_only"] = PlayersOnly,
		["created_at"] = CreatedAt,
		["updated_at"] = UpdatedAt,
		["last_sent_at"] = LastSentAt,
		["last_result"] = LastResult,
		["next_run"] = NextRun,
	};

	/// <summary>Объявление из <c>automation.json</c>; сохранённый срок продолжается.</summary>
	public static Result<ScheduledAnnouncement> FromJson(JsonObject stored, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(stored);

		var draft = Draft(stored);
		var id = Read(stored["id"]);
		var created = Create(draft, id, now);
		if (!created.IsSuccess)
		{
			return created;
		}

		var next = Long(stored["next_run"]);
		return created.Value with
		{
			CreatedAt = Long(stored["created_at"]) is > 0 and var at ? at : created.Value.CreatedAt,
			UpdatedAt = Long(stored["updated_at"]) is > 0 and var updated ? updated : created.Value.UpdatedAt,
			LastSentAt = Long(stored["last_sent_at"]),
			LastResult = Read(stored["last_result"]),
			NextRun = draft.Enabled && next > 0 ? next : created.Value.NextRun,
		};
	}

	/// <summary>Черновик из тела запроса или записи файла.</summary>
	public static AnnouncementDraft Draft(JsonObject values)
	{
		ArgumentNullException.ThrowIfNull(values);

		return new AnnouncementDraft
		{
			Text = Read(values["text"]) ?? string.Empty,
			Enabled = values["enabled"] is not JsonValue enabled || enabled.GetValueKind() != JsonValueKind.False,
			IntervalMinutes = values["interval_minutes"] is JsonValue interval && interval.TryGetValue<int>(out var minutes) ? minutes : 60,
			PlayersOnly = values["players_only"] is not JsonValue players || players.GetValueKind() != JsonValueKind.False,
		};
	}

	private static string? Read(JsonNode? node) =>
		node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

	private static long Long(JsonNode? node) =>
		node is JsonValue value && long.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
			? number
			: 0;
}
