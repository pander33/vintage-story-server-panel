using System.Text.Json.Nodes;
using Panel.Maintenance.Domain;
using Panel.SharedKernel;

namespace Panel.Maintenance.Tests;

/// <summary>
/// Задания и сроки. Время местное для панели, поэтому часовой пояс задаётся явно.
/// </summary>
public sealed class MaintenanceJobTests
{
	/// <summary>UTC+3 без перехода на летнее время.</summary>
	private static readonly TimeZoneInfo zone =
		TimeZoneInfo.CreateCustomTimeZone("panel-test", TimeSpan.FromHours(3), "panel-test", "panel-test");

	/// <summary>Понедельник, 14 сентября 2026, полдень по местному времени.</summary>
	private static readonly DateTimeOffset monday = new(2026, 9, 14, 12, 0, 0, TimeSpan.FromHours(3));

	[Fact]
	public void Еженедельное_задание_получает_срок_в_нужный_день_и_час()
	{
		var job = MaintenanceJob.Create(
			new JobDraft
			{
				Name = "Sunday maintenance",
				ScheduleType = "weekly",
				Time = "04:00",
				Weekdays = [6],
				WarningMinutes = [1, 15, 5, 5],
			},
			id: null,
			monday,
			zone).Value;

		Assert.Equal([15, 5, 1], job.WarningMinutes);

		var next = Local(job.NextRun!.Value);
		Assert.Equal(DayOfWeek.Sunday, next.DayOfWeek);
		Assert.Equal(new DateTime(2026, 9, 20, 4, 0, 0), next.DateTime);

		var following = Schedule.NextRun(job, DateTimeOffset.FromUnixTimeSeconds(job.NextRun.Value + 1), zone);
		Assert.Equal(job.NextRun + (long) TimeSpan.FromDays(7).TotalSeconds, following);
	}

	[Theory]
	[InlineData(11, 26)] // до срока — сегодня
	[InlineData(13, 27)] // после — завтра
	public void Ежедневное_задание_назначается_на_ближайшее_время(int hour, int expectedDay)
	{
		var now = new DateTimeOffset(2026, 9, 26, hour, 0, 0, TimeSpan.FromHours(3));
		var job = MaintenanceJob.Create(new JobDraft { Name = "Daily", Time = "12:00" }, null, now, zone).Value;

		Assert.Equal(new DateTime(2026, 9, expectedDay, 12, 0, 0), Local(job.NextRun!.Value).DateTime);
	}

	[Fact]
	public void План_без_копии_и_без_перезапуска_отклоняется()
	{
		var job = MaintenanceJob.Create(
			new JobDraft { Name = "noop", Backup = false, Restart = false }, null, monday, zone);

		Assert.Equal("Выберите резервную копию или перезапуск", job.Failure!.Message);
	}

	[Fact]
	public void Одноразовый_запуск_в_прошлом_отклоняется_но_читается_из_файла()
	{
		var draft = new JobDraft { Name = "past", ScheduleType = "once", RunAt = "2026-09-14T11:00", Restart = false };

		var created = MaintenanceJob.Create(draft, null, monday, zone);
		Assert.Equal("Дата одноразового запуска должна быть в будущем", created.Failure!.Message);

		// В файле состояния хранятся и прошедшие одноразовые задания, и при загрузке они не
		// теряются.
		var stored = MaintenanceJob.Create(draft, "abc", monday, zone, requireFuture: false);
		Assert.True(stored.IsSuccess);
		Assert.Null(stored.Value.NextRun);
	}

	[Fact]
	public void Первой_показывается_ошибка_названия()
	{
		var job = MaintenanceJob.Create(
			new JobDraft { Name = "  ", ScheduleType = "nonsense", Backup = false, Restart = false },
			null,
			monday,
			zone);

		Assert.Equal("Название должно содержать от 1 до 80 символов", job.Failure!.Message);
	}

	[Theory]
	[InlineData("4", "Время должно быть в формате ЧЧ:ММ")]
	[InlineData("24:00", "Недопустимое время запуска")]
	[InlineData("12:60", "Недопустимое время запуска")]
	public void Время_проверяется(string time, string expected)
	{
		var job = MaintenanceJob.Create(new JobDraft { Name = "Daily", Time = time }, null, monday, zone);

		Assert.Equal(expected, job.Failure!.Message);
	}

	[Theory]
	[InlineData(59)]
	[InlineData(3601)]
	public void Ожидание_готовности_ограничено(int seconds)
	{
		var job = MaintenanceJob.Create(
			new JobDraft { Name = "Daily", ReadinessTimeout = seconds }, null, monday, zone);

		Assert.Equal("Ожидание готовности должно быть от 60 до 3600 секунд", job.Failure!.Message);
	}

	[Fact]
	public void Больше_восьми_предупреждений_нельзя()
	{
		var job = MaintenanceJob.Create(
			new JobDraft { Name = "Daily", WarningMinutes = [1, 2, 3, 4, 5, 6, 7, 8, 9] }, null, monday, zone);

		Assert.Equal("Предупреждения должны быть от 0 до 120 минут, не более восьми", job.Failure!.Message);
	}

	[Fact]
	public void Выключенное_задание_срока_не_имеет()
	{
		var job = MaintenanceJob.Create(new JobDraft { Name = "Daily", Enabled = false }, null, monday, zone).Value;

		Assert.Null(job.NextRun);
	}

	[Fact]
	public void Подошедшее_одноразовое_задание_выключается()
	{
		var job = MaintenanceJob.Create(
			new JobDraft { Name = "once", ScheduleType = "once", RunAt = "2026-09-14T13:00", Restart = false },
			null,
			monday,
			zone).Value;

		var advanced = job.Advance(monday.AddHours(1), zone);

		Assert.False(advanced.Enabled);
		Assert.Null(advanced.NextRun);
	}

	[Fact]
	public void Повторяющееся_задание_переходит_к_сроку_после_текущего()
	{
		var job = MaintenanceJob.Create(new JobDraft { Name = "Daily", Time = "13:00" }, null, monday, zone).Value;
		var due = job.NextRun!.Value;

		var advanced = job.Advance(DateTimeOffset.FromUnixTimeSeconds(due), zone);

		Assert.Equal(due + (long) TimeSpan.FromDays(1).TotalSeconds, advanced.NextRun);
	}

	[Fact]
	public void Изменение_сохраняет_создание_и_итог_последнего_прогона()
	{
		var original = MaintenanceJob.Create(new JobDraft { Name = "Daily" }, "job", monday, zone).Value
			.WithResult(monday.ToUnixTimeSeconds() - 60, RunStatus.Success);

		var later = monday.AddHours(2);
		var changed = MaintenanceJob.Create(
			original.ToDraft() with { Name = "Renamed" }, "job", later, zone, previous: original).Value;

		Assert.Equal("Renamed", changed.Name);
		Assert.Equal(original.CreatedAt, changed.CreatedAt);
		Assert.Equal(later.ToUnixTimeSeconds(), changed.UpdatedAt);
		Assert.Equal("success", changed.LastResult);
		Assert.Equal(original.LastRunAt, changed.LastRunAt);
	}

	[Fact]
	public void Запись_файла_имеет_свои_поля_и_читается_обратно()
	{
		var job = MaintenanceJob.Create(
			new JobDraft { Name = "Sunday", ScheduleType = "weekly", Weekdays = [6, 0], WarningMinutes = [5] },
			"job",
			monday,
			zone).Value;

		var json = job.ToJson();
		Assert.Equal(
			["id", "name", "enabled", "schedule_type", "time", "weekdays", "run_at", "warning_minutes", "backup",
			 "restart", "readiness_timeout", "created_at", "updated_at", "last_run_at", "last_result", "next_run"],
			json.Select(pair => pair.Key));

		var read = MaintenanceJob.FromJson(JsonNode.Parse(json.ToJsonString())!.AsObject(), monday, zone).Value;
		Assert.Equal(job.Id, read.Id);
		Assert.Equal([0, 6], read.Weekdays);
		Assert.Equal(job.CreatedAt, read.CreatedAt);
		Assert.Equal(job.NextRun, read.NextRun);
	}

	[Fact]
	public void Черновик_из_тела_берёт_умолчания_формы_и_только_целые()
	{
		var draft = MaintenanceJob.Draft(JsonNode.Parse(
			"""{"name": "x", "weekdays": [1, "2", 3.5, 4], "backup": false}""")!.AsObject());

		Assert.True(draft.Enabled);
		Assert.Equal("daily", draft.ScheduleType);
		Assert.Equal("04:00", draft.Time);
		Assert.False(draft.Backup);
		Assert.True(draft.Restart);
		Assert.Equal(600, draft.ReadinessTimeout);
		Assert.Equal([1, 4], draft.Weekdays);
	}

	[Fact]
	public void Момент_без_смещения_местный_со_смещением_как_указан()
	{
		Assert.Equal(
			new DateTimeOffset(2026, 9, 14, 13, 0, 0, TimeSpan.FromHours(3)),
			Schedule.ParseMoment("2026-09-14T13:00", zone));
		Assert.Equal(
			new DateTimeOffset(2026, 9, 14, 13, 0, 0, TimeSpan.Zero),
			Schedule.ParseMoment("2026-09-14T13:00+00:00", zone));
		Assert.Null(Schedule.ParseMoment("завтра", zone));
	}

	private static DateTimeOffset Local(long seconds) =>
		TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(seconds), zone);
}

public sealed class MaintenanceRunTests
{
	[Fact]
	public void Итог_выставляется_один_раз()
	{
		var run = new MaintenanceRun("run", "job", "Daily", "manual", 100);
		run.Begin();
		run.Succeed(200);

		Assert.Throws<InvalidOperationException>(() => run.Fail(Failure.Unavailable("поздно"), 300));
		Assert.Equal(RunStatus.Success, run.Status);
	}

	[Fact]
	public void Запись_прогона_в_форме_automation_runs_jsonl()
	{
		var run = new MaintenanceRun("run", "job", "Daily", "schedule", 100);
		run.Begin();
		run.Step(new RunStep("announce", "ok", Minutes: 5));
		run.Step(new RunStep("backup", "ok", BackupName: "backup.tar.gz"));
		run.Fail(Failure.Unavailable("Сервер не перешёл в состояние «Готов» за отведённое время"), 160);

		var json = run.ToJson();

		Assert.Equal(
			["id", "job_id", "name", "trigger", "started_at", "finished_at", "status", "steps", "detail"],
			json.Select(pair => pair.Key));
		Assert.Equal("failed", json["status"]!.GetValue<string>());
		Assert.Equal(
			"""[{"action":"announce","status":"ok","minutes":5},{"action":"backup","status":"ok","name":"backup.tar.gz"}]""",
			json["steps"]!.ToJsonString());
	}

	[Fact]
	public void Пропущенный_срок_объясняется_текстом()
	{
		var run = new MaintenanceRun("run", "job", "Daily", "schedule", 100);
		run.SkipMissed(100);

		Assert.Equal(RunStatus.Skipped, run.Status);
		Assert.Equal("Пропущено: панель была выключена более 6 часов", run.Detail);
	}
}
