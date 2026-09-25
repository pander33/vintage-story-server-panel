using System.Text.Json.Nodes;
using Panel.Configuration.Domain;

namespace Panel.Configuration.Tests;

/// <summary>
/// Шаблон нового мира. Панель не знает всех параметров мира и проверяет только, что JSON
/// разумного размера.
/// </summary>
public sealed class WorldTemplateTests
{
	[Fact]
	public void Не_объект_отвергается() =>
		Assert.Equal(
			"WorldConfiguration должен быть JSON-объектом",
			WorldTemplate.FromJson(JsonValue.Create(1)).Failure!.Message);

	[Fact]
	public void Двенадцать_уровней_допустимы_тринадцать_нет()
	{
		Assert.True(WorldTemplate.FromJson(Nested(12)).IsSuccess);
		Assert.Equal(
			"JSON имеет слишком большую вложенность",
			WorldTemplate.FromJson(Nested(13)).Failure!.Message);
	}

	[Theory]
	[InlineData("{\"a\\u0001\":1}")]
	public void Управляющий_символ_в_ключе_отвергается(string json) =>
		Assert.Equal("Недопустимое имя параметра", WorldTemplate.FromJson(JsonNode.Parse(json)).Failure!.Message);

	[Fact]
	public void Слишком_длинный_ключ_отвергается() =>
		Assert.False(WorldTemplate.FromJson(new JsonObject { [new string('k', 201)] = 1 }).IsSuccess);

	[Fact]
	public void Слишком_большой_массив_отвергается() =>
		Assert.Equal(
			"Слишком большой массив",
			WorldTemplate.FromJson(new JsonObject { ["a"] = new JsonArray([.. Enumerable.Range(0, 20_001).Select(i => (JsonNode?) i)]) }).Failure!.Message);

	[Fact]
	public void Шаблон_читается_из_конфигурации()
	{
		var config = ServerConfiguration.FromJson(JsonNode.Parse(
			"{\"WorldConfig\":{\"Seed\":\"1\",\"WorldConfiguration\":{\"gameMode\":\"survival\"}}}")).Value;

		Assert.Equal("survival", config.WorldTemplate.Document["gameMode"]!.GetValue<string>());
	}

	[Fact]
	public void Без_раздела_шаблон_пуст() =>
		Assert.Empty(ServerConfiguration.FromJson(new JsonObject()).Value.WorldTemplate.Document);

	[Fact]
	public void Замена_шаблона_не_трогает_соседей()
	{
		var config = ServerConfiguration.FromJson(JsonNode.Parse(
			"{\"Port\":1,\"WorldConfig\":{\"Seed\":\"1\",\"WorldConfiguration\":{\"a\":1}}}")).Value;
		var template = WorldTemplate.FromJson(JsonNode.Parse("{\"b\":2}")).Value;

		var changed = config.WithWorldTemplate(template).Value.Document;

		Assert.Equal("1", changed["WorldConfig"]!["Seed"]!.GetValue<string>());
		Assert.Equal(2, changed["WorldConfig"]!["WorldConfiguration"]!["b"]!.GetValue<int>());
		Assert.Null(changed["WorldConfig"]!["WorldConfiguration"]!["a"]);
		Assert.Equal(1, changed["Port"]!.GetValue<int>());
	}

	[Fact]
	public void Раздел_создаётся_если_его_нет() =>
		Assert.NotNull(ServerConfiguration.FromJson(new JsonObject()).Value
			.WithWorldTemplate(WorldTemplate.Empty).Value.Document["WorldConfig"]!["WorldConfiguration"]);

	[Fact]
	public void Раздел_неизвестного_вида_не_перезаписывается() =>
		Assert.Equal(
			"WorldConfig имеет неподдерживаемый формат",
			ServerConfiguration.FromJson(JsonNode.Parse("{\"WorldConfig\":[1]}")).Value
				.WithWorldTemplate(WorldTemplate.Empty).Failure!.Message);

	/// <summary>Объект с глубиной вложенности <paramref name="depth"/> и скаляром на этой глубине.</summary>
	private static JsonObject Nested(int depth)
	{
		JsonNode inner = JsonValue.Create(1);
		for (var level = 1; level < depth; level++)
		{
			inner = new JsonObject { ["x"] = inner };
		}

		return new JsonObject { ["x"] = inner };
	}
}
