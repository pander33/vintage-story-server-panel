using System.Text.Json.Nodes;
using Panel.Maintenance.Domain;
using Panel.SharedKernel;

namespace Panel.Maintenance.Tests;

/// <summary>
/// Объявления по расписанию: проверка формы, сроки и чтение из <c>automation.json</c>.
/// </summary>
public sealed class ScheduledAnnouncementTests
{
	private static readonly DateTimeOffset noon = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Новое_объявление_назначается_через_интервал()
	{
		var item = ScheduledAnnouncement.Create(new AnnouncementDraft { Text = "  Правила: /rules  ", IntervalMinutes = 30 }, null, noon).Value;

		Assert.Equal("Правила: /rules", item.Text);
		Assert.Equal(noon.AddMinutes(30).ToUnixTimeSeconds(), item.NextRun);
		Assert.False(item.IsDue(noon.AddMinutes(29)));
		Assert.True(item.IsDue(noon.AddMinutes(30)));
	}

	[Theory]
	[InlineData("", 60)]
	[InlineData("текст", 4)]
	[InlineData("текст", 1441)]
	[InlineData("строка\nс переводом", 60)]
	public void Недопустимая_форма_отклоняется(string text, int minutes)
	{
		var item = ScheduledAnnouncement.Create(new AnnouncementDraft { Text = text, IntervalMinutes = minutes }, null, noon);

		Assert.False(item.IsSuccess);
		Assert.Equal(FailureCode.Invalid, item.Failure!.Code);
	}

	[Fact]
	public void Выключенное_объявление_не_имеет_срока()
	{
		var item = ScheduledAnnouncement.Create(new AnnouncementDraft { Text = "текст", Enabled = false }, null, noon).Value;

		Assert.Null(item.NextRun);
		Assert.False(item.IsDue(noon.AddDays(1)));
	}

	[Fact]
	public void Правка_текста_не_откладывает_срок_а_смена_интервала_откладывает()
	{
		var item = ScheduledAnnouncement.Create(new AnnouncementDraft { Text = "старый", IntervalMinutes = 60 }, null, noon).Value;
		var later = noon.AddMinutes(40);

		var edited = ScheduledAnnouncement.Create(item.ToDraft() with { Text = "новый" }, item.Id, later, item).Value;
		Assert.Equal(item.NextRun, edited.NextRun);
		Assert.Equal(item.CreatedAt, edited.CreatedAt);

		var retimed = ScheduledAnnouncement.Create(item.ToDraft() with { IntervalMinutes = 15 }, item.Id, later, item).Value;
		Assert.Equal(later.AddMinutes(15).ToUnixTimeSeconds(), retimed.NextRun);
	}

	[Fact]
	public void Пропущенные_сроки_не_догоняются()
	{
		var item = ScheduledAnnouncement.Create(new AnnouncementDraft { Text = "текст", IntervalMinutes = 10 }, null, noon).Value;
		var afterDowntime = noon.AddHours(5);

		var sent = item.Advance(afterDowntime, AnnouncementOutcome.Sent);

		Assert.Equal(afterDowntime.AddMinutes(10).ToUnixTimeSeconds(), sent.NextRun);
		Assert.Equal(afterDowntime.ToUnixTimeSeconds(), sent.LastSentAt);
		Assert.Equal("sent", sent.LastResult);
	}

	[Fact]
	public void Пропуск_не_меняет_время_последней_отправки()
	{
		var item = ScheduledAnnouncement.Create(new AnnouncementDraft { Text = "текст", IntervalMinutes = 10 }, null, noon).Value
			.Advance(noon.AddMinutes(10), AnnouncementOutcome.Sent);

		var skipped = item.Advance(noon.AddMinutes(20), AnnouncementOutcome.Skipped);

		Assert.Equal(noon.AddMinutes(10).ToUnixTimeSeconds(), skipped.LastSentAt);
		Assert.Equal("skipped", skipped.LastResult);
	}

	[Fact]
	public void Объявление_переживает_файл_вместе_со_сроком()
	{
		var item = ScheduledAnnouncement.Create(new AnnouncementDraft { Text = "текст", IntervalMinutes = 10, PlayersOnly = false }, null, noon).Value
			.Advance(noon.AddMinutes(10), AnnouncementOutcome.Sent);

		var restored = ScheduledAnnouncement.FromJson(JsonNode.Parse(item.ToJson().ToJsonString())!.AsObject(), noon.AddHours(1)).Value;

		Assert.Equal(item, restored);
	}

	[Fact]
	public void Черновик_без_полей_берёт_умолчания_формы()
	{
		var draft = ScheduledAnnouncement.Draft(new JsonObject { ["text"] = "текст" });

		Assert.True(draft.Enabled);
		Assert.True(draft.PlayersOnly);
		Assert.Equal(60, draft.IntervalMinutes);
	}
}
