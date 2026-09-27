using Panel.Configuration.Localization;

namespace Panel.Configuration.Tests;

/// <summary>Подписи параметров мира: порядок поиска и устойчивость к пустому и битому JSON.</summary>
public sealed class WorldLanguagePackTests
{
	[Fact]
	public void Подписи_ищут_основной_язык_затем_запасной_затем_данные_игры_затем_код()
	{
		var pack = WorldLanguagePack.From(
			"""{"worldattribute-a": "А", "playstyle-preset-x": "Стиль X", "other-key": "мимо"}""",
			"""{"worldattribute-a": "A", "worldattribute-b": "B"}""");

		Assert.Equal("А", pack.Label("worldattribute-a", "A (fallback)"));
		Assert.Equal("B", pack.Label("worldattribute-b", "B (fallback)"));
		Assert.Equal("C (fallback)", pack.Label("worldattribute-c", "C (fallback)"));
		Assert.Equal("worldattribute-d", pack.Label("worldattribute-d", ""));
		Assert.Null(pack.Find("other-key"));
	}

	[Fact]
	public void Пустой_и_битый_язык_дают_пустой_пакет()
	{
		Assert.Equal("x", WorldLanguagePack.Empty.Label("x", ""));
		Assert.Equal("x", WorldLanguagePack.From(null, "{not json").Label("x", ""));
	}

	[Fact]
	public void Повторяющиеся_ключи_не_роняют_разбор()
	{
		var pack = WorldLanguagePack.From("""{"worldattribute-a": "1", "worldattribute-a": "2"}""", null);

		Assert.Equal("2", pack.Find("worldattribute-a"));
	}

	[Fact]
	public void Слияние_отдаёт_первый_источник_у_которого_есть_ключ()
	{
		var game = WorldLanguagePack.From("""{"worldattribute-shared": "Игра"}""", null);
		var mod = WorldLanguagePack.From("""{"worldattribute-shared": "Мод", "worldattribute-only-mod": "Только мод"}""", null);

		var merged = WorldLanguagePack.Merge([game, mod]);

		Assert.Equal("Игра", merged.Find("worldattribute-shared"));
		Assert.Equal("Только мод", merged.Find("worldattribute-only-mod"));
	}
}
