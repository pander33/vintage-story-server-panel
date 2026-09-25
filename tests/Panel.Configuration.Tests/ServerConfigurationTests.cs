using System.Text.Json.Nodes;
using Panel.Configuration.Domain;

namespace Panel.Configuration.Tests;

/// <summary>
/// Проверка по схеме и дифф. Тексты сообщений сравниваются дословно: они попадают в интерфейс
/// как <c>detail</c> ответа 400 и сверяются с золотыми фикстурами.
/// </summary>
public sealed class ServerConfigurationTests
{
	[Fact]
	public void Не_объект_отвергается() =>
		Assert.Equal(
			"Конфигурация должна быть JSON-объектом",
			ServerConfiguration.FromJson(JsonNode.Parse("[1]")).Failure!.Message);

	[Theory]
	[InlineData("{\"Port\":0}", "Порт: значение меньше 1")]
	[InlineData("{\"Port\":65536}", "Порт: значение больше 65535")]
	[InlineData("{\"DieAboveMemoryUsageMb\":20000000}", "Лимит памяти, МБ: значение больше 1e+07")]
	[InlineData("{\"BlockTickInterval\":4000000}", "Интервал block ticks, мс: значение больше 3.6e+06")]
	[InlineData("{\"Port\":\"42420\"}", "Порт: неверный тип значения")]
	[InlineData("{\"Port\":42420.0}", "Порт: неверный тип значения")]
	[InlineData("{\"Port\":true}", "Порт: неверный тип значения")]
	[InlineData("{\"AllowPvP\":1}", "PvP: неверный тип значения")]
	[InlineData("{\"ServerName\":null}", "Название сервера: неверный тип значения")]
	[InlineData("{\"WhitelistMode\":3}", "Белый список: неверный тип значения")]
	[InlineData("{\"WhitelistMode\":true}", "Белый список: неверный тип значения")]
	[InlineData("{\"WhitelistMode\":1.0}", "Белый список: неверный тип значения")]
	[InlineData("{\"ServerLanguage\":\"en\\n\"}", "Язык сервера: недопустимый формат")]
	[InlineData("{\"ServerLanguage\":\"e\"}", "Язык сервера: недопустимый формат")]
	[InlineData("{\"DefaultRoleCode\":\"su player\"}", "Роль новых игроков: недопустимый формат")]
	[InlineData("{\"SpawnCapPlayerScaling\":1.5}", "Масштаб существ на игрока: значение больше 1")]
	public void Неверное_значение_называет_поле(string json, string expected) =>
		Assert.Equal(expected, Configuration(json).Validate().Failure!.Message);

	[Theory]
	[InlineData("{\"Port\":42420,\"TickTime\":33,\"SpawnCapPlayerScaling\":0.5}")]
	[InlineData("{\"TickTime\":33.3}")]
	[InlineData("{\"ServerUrl\":null,\"ServerDescription\":null}")]
	[InlineData("{\"WhitelistMode\":2,\"ServerLanguage\":\"pt-BR\"}")]
	[InlineData("{\"Password\":\"x\",\"ModPaths\":[\"Mods\"],\"Unknown\":{\"deep\":[1,2]}}")]
	public void Верное_значение_и_чужие_ключи_проходят(string json) =>
		Assert.True(Configuration(json).Validate().IsSuccess);

	/// <summary>Сообщается первое неверное поле в порядке схемы, а не документа.</summary>
	[Fact]
	public void Первым_сообщается_поле_раньшее_по_схеме() =>
		Assert.Equal(
			"Название сервера: неверный тип значения",
			Configuration("{\"Port\":0,\"ServerName\":1}").Validate().Failure!.Message);

	/// <summary>Длина в кодовых точках.</summary>
	[Fact]
	public void Длина_текста_в_кодовых_точках()
	{
		var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 120));
		Assert.True(Configuration(new JsonObject { ["ServerName"] = emoji }).Validate().IsSuccess);

		var longer = new string('я', 121);
		Assert.Equal(
			"Название сервера: текст длиннее 120 символов",
			Configuration(new JsonObject { ["ServerName"] = longer }).Validate().Failure!.Message);
	}

	[Fact]
	public void Предупреждение_не_мешает_чтению()
	{
		var config = Configuration("{\"Port\":0}");

		Assert.Equal("Порт: значение меньше 1", config.ValidationWarning);
		Assert.Null(Configuration("{\"Port\":1}").ValidationWarning);
	}

	/// <summary><c>1</c> и <c>1.0</c>, <c>true</c> и <c>1</c> изменением не считаются.</summary>
	[Theory]
	[InlineData("{\"a\":1}", "{\"a\":1.0}")]
	[InlineData("{\"a\":true}", "{\"a\":1}")]
	[InlineData("{\"a\":{\"x\":1,\"y\":2}}", "{\"a\":{\"y\":2,\"x\":1}}")]
	[InlineData("{\"a\":null}", "{}")]
	public void Равные_по_python_значения_не_дают_изменений(string before, string after) =>
		Assert.Empty(Configuration(before).DiffTo(Configuration(after)));

	[Fact]
	public void Порядок_элементов_массива_значим() =>
		Assert.Single(Configuration("{\"a\":[1,2]}").DiffTo(Configuration("{\"a\":[2,1]}")));

	[Fact]
	public void Дифф_несёт_подпись_и_необходимость_перезапуска()
	{
		var changes = Configuration("{\"Port\":42420,\"OnlyWhitelisted\":false,\"Custom\":1}")
			.DiffTo(Configuration("{\"Port\":42421,\"OnlyWhitelisted\":true,\"Custom\":2}"));

		Assert.Collection(
			changes,
			custom =>
			{
				Assert.Equal("Custom", custom.Key);
				Assert.Equal("Custom", custom.Label);
				Assert.True(custom.RestartRequired);
			},
			whitelist =>
			{
				Assert.Equal("OnlyWhitelisted", whitelist.Key);
				Assert.False(whitelist.RestartRequired);
			},
			port =>
			{
				Assert.Equal("Порт", port.Label);
				Assert.Equal("42420", port.Before!.ToJsonString());
				Assert.Equal("42421", port.After!.ToJsonString());
			});
	}

	/// <summary>Сортировка без учёта регистра: <c>sorted(keys, key=str.casefold)</c>.</summary>
	[Fact]
	public void Дифф_сортируется_без_учёта_регистра()
	{
		var changes = Configuration("{}").DiffTo(Configuration("{\"b\":1,\"A\":1,\"C\":1}"));

		Assert.Equal(["A", "b", "C"], changes.Select(change => change.Key));
	}

	[Fact]
	public void Пароль_в_диффе_скрыт()
	{
		var change = Assert.Single(Configuration("{\"Password\":null}").DiffTo(Configuration("{\"Password\":\"secret\"}")));

		Assert.Null(change.Before);
		Assert.Equal("••••••", change.After!.GetValue<string>());
	}

	[Fact]
	public void Пароль_скрыт_от_тех_кто_не_может_менять()
	{
		var config = Configuration("{\"Password\":\"secret\",\"Port\":1}");
		var redacted = config.RedactedDocument();

		Assert.Equal("••••••", redacted["Password"]!.GetValue<string>());
		Assert.Equal("secret", config.Document["Password"]!.GetValue<string>());

		// Ревизия считается по исходному документу: скрытие пароля не должно делать конфигурацию
		// изменённой для редактора.
		Assert.Equal(config.Revision, Configuration("{\"Password\":\"secret\",\"Port\":1}").Revision);
	}

	[Fact]
	public void Пустой_пароль_не_маскируется() =>
		Assert.Null(Configuration("{\"Password\":null}").RedactedDocument()["Password"]);

	private static ServerConfiguration Configuration(string json) => Configuration(JsonNode.Parse(json)!);

	private static ServerConfiguration Configuration(JsonNode node) => ServerConfiguration.FromJson(node).Value;
}
