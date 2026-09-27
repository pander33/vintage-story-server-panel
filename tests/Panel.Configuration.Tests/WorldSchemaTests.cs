using Panel.Configuration.Domain;

namespace Panel.Configuration.Tests;

/// <summary>Разбор схемы параметров мира: настоящая строка из <c>VSSurvivalMod.dll</c> 1.22.7 и нападения.</summary>
public sealed class WorldSchemaTests
{
	private static string Fixture(string name)
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			if (File.Exists(Path.Combine(directory.FullName, "VintageStoryPanel.slnx")))
			{
				return File.ReadAllText(Path.Combine(directory.FullName, "tests", "Panel.Configuration.Tests", "Fixtures", name));
			}
		}

		throw new InvalidOperationException("Не найден корень репозитория (VintageStoryPanel.slnx)");
	}

	[Fact]
	public void Настоящая_строка_survival_разбирается_целиком()
	{
		var parsed = WorldSchema.ParseSource(Fixture("survival-worldconfig-1.22.7.json"), "game");

		Assert.True(parsed.IsSuccess, parsed.Failure?.Message);
		Assert.Equal(4, parsed.Value.Playstyles.Count);
		Assert.Equal(64, parsed.Value.Attributes.Count);

		var surviveAndBuild = parsed.Value.Playstyles.Single(style => style.Code == "surviveandbuild");
		Assert.Equal("preset-surviveandbuild", surviveAndBuild.LanguageCode);
		Assert.Equal("survival", surviveAndBuild.PlayListCode);
		Assert.Equal("standard", surviveAndBuild.WorldType);
		Assert.Equal(["game", "survival"], surviveAndBuild.Mods);
		Assert.Equal("realistic", surviveAndBuild.Values["worldClimate"]);

		var gameMode = parsed.Value.Attributes.Single(attribute => attribute.Code == "gameMode");
		Assert.Equal(WorldAttributeType.Dropdown, gameMode.Type);
		Assert.Equal("spawnndeath", gameMode.Category);
		Assert.Equal(["survival", "creative"], gameMode.Values);
		Assert.Equal(["Survival", "Creative"], gameMode.Names);
		Assert.Equal("survival", gameMode.Default);
		Assert.False(gameMode.OnlyDuringWorldCreate);

		Assert.Contains(parsed.Value.Attributes, attribute => attribute.OnlyDuringWorldCreate && attribute.Code == "worldWidth");
		Assert.All(parsed.Value.Attributes, attribute => Assert.Equal("game", attribute.Source));

		// Игра пишет типы строчными: 13 переключателей и 51 выбор, неизвестных нет.
		Assert.Equal(13, parsed.Value.Attributes.Count(attribute => attribute.Type == WorldAttributeType.Bool));
		Assert.Equal(51, parsed.Value.Attributes.Count(attribute => attribute.Type == WorldAttributeType.Dropdown));
		Assert.DoesNotContain(parsed.Value.Attributes, attribute => attribute.Type == WorldAttributeType.Unknown);
		Assert.Equal(15, parsed.Value.Attributes.Count(attribute => attribute.OnlyDuringWorldCreate));
	}

	[Fact]
	public void Пустой_источник_даёт_пустую_схему()
	{
		Assert.Equal(WorldSchema.Empty, WorldSchema.ParseSource("", "game").Value);
		Assert.Equal(WorldSchema.Empty, WorldSchema.ParseSource("   ", "mod:x").Value);
	}

	[Fact]
	public void Битый_json_отказывает_с_указанием_источника()
	{
		var result = WorldSchema.ParseSource("{not json", "mod:broken");

		Assert.False(result.IsSuccess);
		Assert.Contains("mod:broken", result.Failure!.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Повторяющийся_ключ_отказывает_а_не_бросает()
	{
		var result = WorldSchema.ParseSource("""{"playstyles":[{"code":"a","worldConfig":{"x":"1","x":"2"}}]}""", "mod:dup");

		Assert.False(result.IsSuccess);
		Assert.Contains("mod:dup", result.Failure!.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Источник_не_объект_отказывает()
	{
		var result = WorldSchema.ParseSource("[1,2,3]", "mod:array");

		Assert.False(result.IsSuccess);
		Assert.Contains("объект", result.Failure!.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Запись_без_code_пропускается_а_не_роняет_разбор()
	{
		var json = """{"playstyles":[{"langCode":"x"}],"worldConfigAttributes":[{"category":"a"}]}""";

		var parsed = WorldSchema.ParseSource(json, "mod:x").Value;

		Assert.Empty(parsed.Playstyles);
		Assert.Empty(parsed.Attributes);
	}

	[Fact]
	public void Неизвестный_datatype_даёт_Unknown_а_не_отказ()
	{
		var json = """{"worldConfigAttributes":[{"code":"custom","dataType":"SomethingNew"}]}""";

		var attribute = WorldSchema.ParseSource(json, "mod:x").Value.Attributes.Single();

		Assert.Equal(WorldAttributeType.Unknown, attribute.Type);
	}

	[Fact]
	public void Ключи_вне_схемы_у_творческого_стиля_сохраняются()
	{
		var json = """{"playstyles":[{"code":"creativebuilding","langCode":"creativebuilding","worldConfig":{"hoursPerDay":"24","cloudypos":"0.5"}}]}""";

		var style = WorldSchema.ParseSource(json, "game").Value.Playstyles.Single();

		Assert.Equal("24", style.Values["hoursPerDay"]);
		Assert.Equal("0.5", style.Values["cloudypos"]);
	}

	// ---------- слияние источников ----------

	[Fact]
	public void Слияние_берёт_игру_первой_и_пропускает_совпадающий_код_мода()
	{
		var game = WorldSchema.ParseSource("""{"worldConfigAttributes":[{"code":"gameMode","dataType":"Bool"}]}""", "game");
		var mod = WorldSchema.ParseSource("""{"worldConfigAttributes":[{"code":"gameMode","dataType":"String"},{"code":"modOnly","dataType":"String"}]}""", "mod:x");

		var merged = WorldSchema.Merge([game, mod]);

		Assert.Equal(2, merged.Attributes.Count);
		Assert.Equal(WorldAttributeType.Bool, merged.Attributes.Single(a => a.Code == "gameMode").Type);
		Assert.Contains(merged.Warnings, warning => warning.Contains("gameMode", StringComparison.Ordinal));
	}

	[Fact]
	public void Отказ_одного_источника_уходит_в_предупреждение_а_не_роняет_остальные()
	{
		var broken = WorldSchema.ParseSource("{not json", "mod:broken");
		var good = WorldSchema.ParseSource("""{"worldConfigAttributes":[{"code":"ok","dataType":"Bool"}]}""", "game");

		var merged = WorldSchema.Merge([good, broken]);

		Assert.Single(merged.Attributes);
		Assert.Single(merged.Warnings);
	}

	[Fact]
	public void Стили_идут_в_порядке_listOrder()
	{
		var first = WorldSchema.ParseSource("""{"playstyles":[{"code":"b","listOrder":5}]}""", "game");
		var second = WorldSchema.ParseSource("""{"playstyles":[{"code":"a","listOrder":1}]}""", "mod:x");

		var merged = WorldSchema.Merge([first, second]);

		Assert.Equal(["a", "b"], merged.Playstyles.Select(style => style.Code));
	}

	// ---------- проверка значения ----------

	[Theory]
	[InlineData(WorldAttributeType.Bool, "true", true)]
	[InlineData(WorldAttributeType.Bool, "maybe", false)]
	[InlineData(WorldAttributeType.Integer, "42", true)]
	[InlineData(WorldAttributeType.Integer, "4.2", false)]
	[InlineData(WorldAttributeType.Number, "4.2", true)]
	[InlineData(WorldAttributeType.Text, "anything", true)]
	[InlineData(WorldAttributeType.Unknown, "anything", true)]
	public void Проверка_значения_по_типу(WorldAttributeType type, string value, bool expected)
	{
		var attribute = new WorldAttribute("x", type, "cat", [], [], null, false, "game");

		Assert.Equal(expected, WorldSchema.ParseValue(attribute, value).IsSuccess);
	}

	[Fact]
	public void Проверка_dropdown_смотрит_список_значений()
	{
		var attribute = new WorldAttribute("x", WorldAttributeType.Dropdown, "cat", ["a", "b"], [], null, false, "game");

		Assert.True(WorldSchema.ParseValue(attribute, "a").IsSuccess);
		Assert.False(WorldSchema.ParseValue(attribute, "c").IsSuccess);
	}

	[Fact]
	public void Отказ_называет_параметр()
	{
		var attribute = new WorldAttribute("myAttr", WorldAttributeType.Bool, "cat", [], [], null, false, "game");

		Assert.Contains("myAttr", WorldSchema.ParseValue(attribute, "maybe").Failure!.Message, StringComparison.Ordinal);
	}
}
