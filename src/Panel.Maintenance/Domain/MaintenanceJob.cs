using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.SharedKernel;

namespace Panel.Maintenance.Domain;

/// <summary>Как повторяется обслуживание.</summary>
public enum ScheduleType
{
	Daily,
	Weekly,
	Once,
}

public static class ScheduleTypeWire
{
	public static string ToWire(ScheduleType type) => type switch
	{
		ScheduleType.Daily => "daily",
		ScheduleType.Weekly => "weekly",
		_ => "once",
	};

	public static Result<ScheduleType> Parse(string? raw) => raw switch
	{
		"daily" => ScheduleType.Daily,
		"weekly" => ScheduleType.Weekly,
		"once" => ScheduleType.Once,
		_ => Failure.Invalid("Неизвестный тип расписания"),
	};
}

/// <summary>
/// Данные формы расписания с умолчаниями формы. Поля, которые форма не прислала при
/// переключении, берутся из задания.
/// </summary>
public sealed record JobDraft
{
	public string Name { get; init; } = string.Empty;
	public bool Enabled { get; init; } = true;
	public string ScheduleType { get; init; } = "daily";
	public string Time { get; init; } = "04:00";
	public IReadOnlyList<int> Weekdays { get; init; } = [];
	public string RunAt { get; init; } = string.Empty;
	public IReadOnlyList<int> WarningMinutes { get; init; } = [];
	public bool Backup { get; init; } = true;
	public bool Restart { get; init; } = true;
	public int ReadinessTimeout { get; init; } = 600;
}

/// <summary>
/// Задание планового обслуживания: когда, с предупреждениями ли, с копией ли, с перезапуском
/// ли. Поля как у элемента <c>jobs</c> в <c>automation.json</c>.
/// </summary>
/// <remarks>
/// Проверки идут в фиксированном порядке: форма показывает первую найденную ошибку.
/// </remarks>
public sealed record MaintenanceJob
{
	private const int maxName = 80;
	private const int maxWarnings = 8;

	public required string Id { get; init; }
	public required string Name { get; init; }
	public required bool Enabled { get; init; }
	public required ScheduleType Type { get; init; }
	public required string Time { get; init; }
	public required IReadOnlyList<int> Weekdays { get; init; }
	public required string RunAt { get; init; }

	/// <summary>Минуты до начала, в которые игроки получат предупреждение; по убыванию.</summary>
	public required IReadOnlyList<int> WarningMinutes { get; init; }

	public required bool Backup { get; init; }
	public required bool Restart { get; init; }
	public required int ReadinessTimeoutSeconds { get; init; }
	public required long CreatedAt { get; init; }
	public required long UpdatedAt { get; init; }
	public long LastRunAt { get; init; }
	public string? LastResult { get; init; }
	public long? NextRun { get; init; }

	/// <summary>Новое или изменённое задание. <paramref name="requireFuture"/> ложно при чтении файла.</summary>
	public static Result<MaintenanceJob> Create(
		JobDraft draft,
		string? id,
		DateTimeOffset now,
		TimeZoneInfo zone,
		bool requireFuture = true,
		MaintenanceJob? previous = null)
	{
		ArgumentNullException.ThrowIfNull(draft);
		ArgumentNullException.ThrowIfNull(zone);

		var name = (draft.Name ?? string.Empty).Trim();
		if (name.Length == 0 || name.EnumerateRunes().Count() > maxName || name.Any(symbol => symbol < ' '))
		{
			return Failure.Invalid("Название должно содержать от 1 до 80 символов");
		}

		var type = ScheduleTypeWire.Parse(draft.ScheduleType);
		if (!type.IsSuccess)
		{
			return Result.Fail<MaintenanceJob>(type.Failure!);
		}

		var job = new MaintenanceJob
		{
			Id = id ?? Guid.NewGuid().ToString("N"),
			Name = name,
			Enabled = draft.Enabled,
			Type = type.Value,
			Time = draft.Time ?? "04:00",
			Weekdays = [.. draft.Weekdays.Distinct().Order()],
			RunAt = draft.RunAt ?? string.Empty,
			WarningMinutes = [.. draft.WarningMinutes.Distinct().OrderDescending()],
			Backup = draft.Backup,
			Restart = draft.Restart,
			ReadinessTimeoutSeconds = draft.ReadinessTimeout,
			CreatedAt = previous?.CreatedAt is > 0 and var created ? created : now.ToUnixTimeSeconds(),
			UpdatedAt = now.ToUnixTimeSeconds(),
			LastRunAt = previous?.LastRunAt ?? 0,
			LastResult = previous?.LastResult,
		};

		if (job.Type is ScheduleType.Daily or ScheduleType.Weekly)
		{
			var clock = Schedule.ParseClock(job.Time);
			if (!clock.IsSuccess)
			{
				return Result.Fail<MaintenanceJob>(clock.Failure!);
			}
		}

		if (job.Type == ScheduleType.Weekly && (job.Weekdays.Count == 0 || job.Weekdays.Any(day => day is < 0 or > 6)))
		{
			return Failure.Invalid("Выберите хотя бы один день недели");
		}

		if (job.Type == ScheduleType.Once)
		{
			if (Schedule.ParseMoment(job.RunAt, zone) is not { } runAt)
			{
				return Failure.Invalid("Укажите дату и время одноразового запуска");
			}

			if (requireFuture && runAt <= now)
			{
				return Failure.Invalid("Дата одноразового запуска должна быть в будущем");
			}
		}

		if (job.WarningMinutes.Any(minutes => minutes is < 0 or > 120) || job.WarningMinutes.Count > maxWarnings)
		{
			return Failure.Invalid("Предупреждения должны быть от 0 до 120 минут, не более восьми");
		}

		if (!job.Backup && !job.Restart)
		{
			return Failure.Invalid("Выберите резервную копию или перезапуск");
		}

		if (job.ReadinessTimeoutSeconds is < 60 or > 3600)
		{
			return Failure.Invalid("Ожидание готовности должно быть от 60 до 3600 секунд");
		}

		return job with { NextRun = job.Enabled ? Schedule.NextRun(job, now.AddSeconds(-1), zone) : null };
	}

	/// <summary>Черновик из задания для изменения и переключения.</summary>
	public JobDraft ToDraft() => new()
	{
		Name = Name,
		Enabled = Enabled,
		ScheduleType = ScheduleTypeWire.ToWire(Type),
		Time = Time,
		Weekdays = Weekdays,
		RunAt = RunAt,
		WarningMinutes = WarningMinutes,
		Backup = Backup,
		Restart = Restart,
		ReadinessTimeout = ReadinessTimeoutSeconds,
	};

	/// <summary>Итог прогона: время начала и статус записываются в задание.</summary>
	public MaintenanceJob WithResult(long startedAt, RunStatus status) =>
		this with { LastRunAt = startedAt, LastResult = RunStatusWire.ToWire(status) };

	/// <summary>
	/// Срок задания наступил. Одноразовое задание выключается, повторяющееся переходит к
	/// следующему сроку.
	/// </summary>
	public MaintenanceJob Advance(DateTimeOffset now, TimeZoneInfo zone)
	{
		ArgumentNullException.ThrowIfNull(zone);

		if (Type == ScheduleType.Once)
		{
			return this with { Enabled = false, NextRun = null };
		}

		var after = Math.Max(now.ToUnixTimeSeconds(), (NextRun ?? 0) + 1);
		return this with { NextRun = Schedule.NextRun(this, DateTimeOffset.FromUnixTimeSeconds(after), zone) };
	}

	public JsonObject ToJson() => new()
	{
		["id"] = Id,
		["name"] = Name,
		["enabled"] = Enabled,
		["schedule_type"] = ScheduleTypeWire.ToWire(Type),
		["time"] = Time,
		["weekdays"] = new JsonArray([.. Weekdays.Select(day => (JsonNode?) day)]),
		["run_at"] = RunAt,
		["warning_minutes"] = new JsonArray([.. WarningMinutes.Select(minutes => (JsonNode?) minutes)]),
		["backup"] = Backup,
		["restart"] = Restart,
		["readiness_timeout"] = ReadinessTimeoutSeconds,
		["created_at"] = CreatedAt,
		["updated_at"] = UpdatedAt,
		["last_run_at"] = LastRunAt,
		["last_result"] = LastResult,
		["next_run"] = NextRun,
	};

	/// <summary>Задание из <c>automation.json</c>; срок в прошлом допустим.</summary>
	public static Result<MaintenanceJob> FromJson(JsonObject stored, DateTimeOffset now, TimeZoneInfo zone)
	{
		ArgumentNullException.ThrowIfNull(stored);

		var previous = new MaintenanceJob
		{
			Id = string.Empty,
			Name = string.Empty,
			Enabled = false,
			Type = ScheduleType.Daily,
			Time = string.Empty,
			Weekdays = [],
			RunAt = string.Empty,
			WarningMinutes = [],
			Backup = false,
			Restart = false,
			ReadinessTimeoutSeconds = 0,
			CreatedAt = Long(stored["created_at"]),
			UpdatedAt = 0,
			LastRunAt = Long(stored["last_run_at"]),
			LastResult = stored["last_result"] is JsonValue result && result.GetValueKind() == JsonValueKind.String
				? result.GetValue<string>()
				: null,
		};

		var id = stored["id"] is JsonValue identifier && identifier.GetValueKind() == JsonValueKind.String
			? identifier.GetValue<string>()
			: null;

		return Create(Draft(stored), id, now.AddSeconds(-1), zone, requireFuture: false, previous);
	}

	/// <summary>Черновик из объекта JSON: тела запроса или записи файла.</summary>
	public static JobDraft Draft(JsonObject values)
	{
		ArgumentNullException.ThrowIfNull(values);

		return new JobDraft
		{
			Name = Text(values["name"]) ?? string.Empty,
			Enabled = values["enabled"] is not JsonValue enabled || enabled.GetValueKind() != JsonValueKind.False,
			ScheduleType = Text(values["schedule_type"]) ?? "daily",
			Time = Text(values["time"]) ?? "04:00",
			Weekdays = Integers(values["weekdays"]),
			RunAt = Text(values["run_at"]) ?? string.Empty,
			WarningMinutes = Integers(values["warning_minutes"]),
			Backup = values["backup"] is not JsonValue backup || backup.GetValueKind() != JsonValueKind.False,
			Restart = values["restart"] is not JsonValue restart || restart.GetValueKind() != JsonValueKind.False,
			ReadinessTimeout = values["readiness_timeout"] is JsonValue timeout && timeout.TryGetValue<int>(out var seconds)
				? seconds
				: 600,
		};
	}

	private static string? Text(JsonNode? node) =>
		node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

	/// <summary>Только целые числа, остальное отбрасывается.</summary>
	private static IReadOnlyList<int> Integers(JsonNode? node) =>
		node is JsonArray items
			? [.. items.OfType<JsonValue>()
				.Where(item => item.GetValueKind() == JsonValueKind.Number)
				.Select(item => int.TryParse(item.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ? (int?)number : null)
				.OfType<int>()]
			: [];

	private static long Long(JsonNode? node) =>
		node is JsonValue value && long.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
			? number
			: 0;
}

/// <summary>
/// Сроки заданий. Время местное для панели: «в 04:00» администратор понимает по своим часам,
/// а не по UTC.
/// </summary>
public static class Schedule
{
	public static Result<(int Hour, int Minute)> ParseClock(string? value)
	{
		var parts = (value ?? string.Empty).Split(':');
		if (parts.Length != 2
			|| !int.TryParse(parts[0].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var hour)
			|| !int.TryParse(parts[1].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var minute))
		{
			return Failure.Invalid("Время должно быть в формате ЧЧ:ММ");
		}

		return hour is < 0 or > 23 || minute is < 0 or > 59
			? Failure.Invalid("Недопустимое время запуска")
			: (hour, minute);
	}

	/// <summary>
	/// Дата и время одноразового запуска. Без смещения это местное время панели, как
	/// <c>datetime.fromisoformat(...).timestamp()</c>; со смещением — как указано.
	/// </summary>
	public static DateTimeOffset? ParseMoment(string? value, TimeZoneInfo zone)
	{
		ArgumentNullException.ThrowIfNull(zone);

		var text = (value ?? string.Empty).Trim();
		if (text.Length < 10)
		{
			return null;
		}

		if (DateTimeOffset.TryParseExact(
				text,
				["yyyy-MM-dd'T'HH:mmzzz", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ssK"],
				CultureInfo.InvariantCulture,
				DateTimeStyles.None,
				out var absolute)
			&& (text.EndsWith('Z') || text.LastIndexOfAny(['+', '-']) > 10))
		{
			return absolute;
		}

		if (!DateTime.TryParseExact(
				text,
				["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"],
				CultureInfo.InvariantCulture,
				DateTimeStyles.None,
				out var local))
		{
			return null;
		}

		return Local(local, zone);
	}

	/// <summary>Следующий срок строго после <paramref name="after"/>; <c>null</c>, если сроков больше нет.</summary>
	public static long? NextRun(MaintenanceJob job, DateTimeOffset after, TimeZoneInfo zone)
	{
		ArgumentNullException.ThrowIfNull(job);
		ArgumentNullException.ThrowIfNull(zone);

		if (job.Type == ScheduleType.Once)
		{
			return ParseMoment(job.RunAt, zone) is { } moment && moment > after
				? (long) Math.Ceiling(moment.ToUnixTimeMilliseconds() / 1000d)
				: null;
		}

		var clock = ParseClock(job.Time);
		if (!clock.IsSuccess)
		{
			return null;
		}

		var (hour, minute) = clock.Value;
		var current = TimeZoneInfo.ConvertTime(after, zone).DateTime;

		if (job.Type == ScheduleType.Daily)
		{
			var candidate = Local(current.Date.AddHours(hour).AddMinutes(minute), zone);
			if (candidate <= after)
			{
				candidate = Local(current.Date.AddDays(1).AddHours(hour).AddMinutes(minute), zone);
			}

			return candidate.ToUnixTimeSeconds();
		}

		for (var offset = 0; offset < 8; offset++)
		{
			var day = current.Date.AddDays(offset);
			var candidate = Local(day.AddHours(hour).AddMinutes(minute), zone);

			// Понедельник — 0.
			var weekday = ((int) day.DayOfWeek + 6) % 7;
			if (job.Weekdays.Contains(weekday) && candidate > after)
			{
				return candidate.ToUnixTimeSeconds();
			}
		}

		return null;
	}

	/// <summary>
	/// Местное время как момент. Несуществующее время (переход на летнее) сдвигается на час
	/// вперёд.
	/// </summary>
	private static DateTimeOffset Local(DateTime wall, TimeZoneInfo zone)
	{
		var unspecified = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
		if (zone.IsInvalidTime(unspecified))
		{
			unspecified = unspecified.AddHours(1);
		}

		return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
	}
}
