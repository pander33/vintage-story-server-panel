using System.Text.Json.Nodes;
using Panel.Observability.Domain;
using Panel.SharedKernel;

namespace Panel.Observability.Tests;

/// <summary>События и уведомления без сети и файлов.</summary>
public sealed class PanelEventTests
{
	private static readonly DateTimeOffset now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Событие_обрезает_тексты_до_пределов()
	{
		var record = PanelEvent.Create(
			"server.stopped", EventSeverity.Error, Phrase.From(new string('т', 200)), Phrase.From(new string('д', 3000)), new string('и', 50), now).Value;

		Assert.Equal((160, 2000, 40), (record.Title.ToString().Length, record.Detail.ToString().Length, record.Source.Length));
		Assert.Equal(32, record.Id.Length);
		Assert.Equal("error", record.ToJson()["severity"]!.GetValue<string>());
	}

	[Theory]
	[InlineData("")]
	[InlineData("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
	public void Недопустимый_вид_отвергается(string kind) =>
		Assert.False(PanelEvent.Create(kind, EventSeverity.Info, Phrase.From("t"), null, "panel", now).IsSuccess);

	[Fact]
	public void Ключ_паузы_по_умолчанию_вид_события() =>
		Assert.Equal("disk.low", PanelEvent.Create("disk.low", EventSeverity.Critical, Phrase.From("t"), null, "m", now).Value.NotificationKey);

	[Theory]
	[InlineData("all", "", 3)]
	[InlineData("error", "", 1)]
	[InlineData("all", "ДИСК", 1)]
	public void Фильтр_ленты(string severity, string search, int expected)
	{
		JsonObject[] items =
		[
			new() { ["severity"] = "error", ["title"] = "Сервер остановлен", ["detail"] = "" },
			new() { ["severity"] = "critical", ["title"] = "Заканчивается место на диске", ["detail"] = "" },
			new() { ["severity"] = "info", ["title"] = "Игрок вошёл", ["detail"] = "Alice" },
		];

		Assert.Equal(expected, EventFilter.Create(severity, search).Value.Apply(items, 100).Count);
	}

	[Fact]
	public void Неверный_фильтр_отвергается() =>
		Assert.Equal("Недопустимый фильтр событий", EventFilter.Create("fatal", "").Failure!.Message);

	[Fact]
	public void Настройки_проверяются_замороженными_текстами()
	{
		var settings = NotificationSettings.Defaults;

		Assert.Equal("Неизвестный уровень уведомлений",
			settings.Apply(Update(minimumSeverity: "debug")).Failure!.Message);
		Assert.Equal("Пауза уведомлений должна быть от 30 до 86400 секунд",
			settings.Apply(Update(cooldown: 10)).Failure!.Message);
		Assert.Equal("Webhook должен использовать HTTPS",
			settings.Apply(Update(webhook: "http://example.org/hook")).Failure!.Message);
		Assert.Equal("Недопустимое значение telegram_token",
			settings.Apply(Update(telegramToken: "a\nb")).Failure!.Message);
	}

	/// <summary>Форма не знает старого секрета: <c>null</c> его сохраняет, пустая строка стирает.</summary>
	[Fact]
	public void Секрет_null_сохраняется_пустой_стирается()
	{
		var configured = NotificationSettings.Defaults
			.Apply(Update(webhook: "https://example.org/hook", telegramToken: "123:abc")).Value;

		var kept = configured.Apply(Update()).Value;
		var cleared = configured.Apply(Update(webhook: string.Empty)).Value;

		Assert.Equal("https://example.org/hook", kept.WebhookUrl);
		Assert.Equal("123:abc", kept.TelegramToken);
		Assert.Equal(string.Empty, cleared.WebhookUrl);
	}

	[Fact]
	public void Открытая_часть_не_несёт_секретов()
	{
		var settings = NotificationSettings.Defaults
			.Apply(Update(telegramToken: "123:secret", telegramChat: "42")).Value;

		var json = settings.Public().ToJsonString();

		Assert.DoesNotContain("123:secret", json, StringComparison.Ordinal);
		Assert.True(settings.Public()["telegram_token_configured"]!.GetValue<bool>());
		Assert.False(settings.Public()["webhook_url_configured"]!.GetValue<bool>());
	}

	[Fact]
	public void Незнакомые_ключи_файла_переживают_сохранение()
	{
		var stored = new JsonObject { ["enabled"] = true, ["future_option"] = 7 };

		var saved = NotificationSettings.FromJson(stored).Apply(Update(enabled: true)).Value.ToJson();

		Assert.Equal(7, saved["future_option"]!.GetValue<int>());
	}

	[Fact]
	public void Пауза_и_порог_уведомлений()
	{
		var settings = NotificationSettings.Defaults.Apply(Update(enabled: true, cooldown: 300)).Value;
		var gate = new NotificationGate();

		Assert.False(gate.Admit(Event("player.joined", EventSeverity.Info, now), true, settings));
		Assert.True(gate.Admit(Event("disk.low", EventSeverity.Critical, now), true, settings));
		Assert.False(gate.Admit(Event("disk.low", EventSeverity.Critical, now.AddSeconds(100)), true, settings));
		Assert.True(gate.Admit(Event("disk.low", EventSeverity.Critical, now.AddSeconds(300)), true, settings));
		Assert.False(gate.Admit(Event("server.stopped", EventSeverity.Error, now), notify: false, settings));
	}

	[Fact]
	public void Выключенные_уведомления_не_отправляются() =>
		Assert.False(new NotificationGate().Admit(Event("disk.low", EventSeverity.Critical, now), true, NotificationSettings.Defaults));

	/// <summary>Разные ключи паузы (разные игроки с плохим пингом) не гасят друг друга.</summary>
	[Fact]
	public void Пауза_по_ключу_дедупликации()
	{
		var settings = NotificationSettings.Defaults.Apply(Update(enabled: true)).Value;
		var gate = new NotificationGate();

		Assert.True(gate.Admit(Ping("Alice"), true, settings));
		Assert.True(gate.Admit(Ping("Bob"), true, settings));
		Assert.False(gate.Admit(Ping("Alice"), true, settings));

		static PanelEvent Ping(string name) => PanelEvent.Create(
			"player.ping_critical", EventSeverity.Warning, Phrase.From("t"), null, "telemetry", now, $"player.ping_critical:{name}").Value;
	}

	[Fact]
	public void Сообщения_каналов()
	{
		var settings = NotificationSettings.Defaults.Apply(Update(
			webhook: "https://hook.example/x",
			discord: "https://discord.example/y",
			telegramToken: "123:abc",
			telegramChat: "42")).Value;
		var record = Event("disk.low", EventSeverity.Critical, now, "Свободно 3 ГБ").ToJson();

		var deliveries = NotificationMessage.For(record, settings, phrase => phrase.ToString());

		Assert.Equal(3, deliveries.Count);
		Assert.Equal("disk.low", deliveries[0].Payload["kind"]!.GetValue<string>());
		Assert.Equal("[CRITICAL] t\nСвободно 3 ГБ", deliveries[1].Payload["content"]!.GetValue<string>());
		Assert.Equal("https://api.telegram.org/bot123:abc/sendMessage", deliveries[2].Url);
		Assert.Equal("42", deliveries[2].Payload["chat_id"]!.GetValue<string>());
	}

	[Fact]
	public void Без_каналов_доставлять_некуда() =>
		Assert.Empty(NotificationMessage.For(Event("x", EventSeverity.Info, now).ToJson(), NotificationSettings.Defaults, phrase => phrase.ToString()));

	private static PanelEvent Event(string kind, EventSeverity severity, DateTimeOffset at, string detail = "") =>
		PanelEvent.Create(kind, severity, Phrase.From("t"), Phrase.From(detail), "test", at).Value;

	private static NotificationUpdate Update(
		bool enabled = false,
		string? minimumSeverity = "warning",
		int? cooldown = 300,
		string? webhook = null,
		string? discord = null,
		string? telegramToken = null,
		string? telegramChat = null) =>
		new(enabled, minimumSeverity, cooldown, webhook, discord, telegramToken, telegramChat);
}
