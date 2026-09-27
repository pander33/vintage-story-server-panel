using System.Text.Json;
using Panel.Runtime.Domain;

namespace Panel.Runtime.Tests;

/// <summary>
/// Разбор <c>playersbanned.json</c> и <c>playerswhitelisted.json</c> (интерфейс 2.1 §3). Формат
/// файлов не сверен с настоящим сервером, поэтому разбор терпим к написанию полей.
/// </summary>
public sealed class PlayerListsTests
{
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("[]")]
	[InlineData("{}")]
	public void Пустой_файл_это_пустой_список(string json)
	{
		Assert.Empty(PlayerLists.ParseEntries(json));
	}

	[Fact]
	public void Бан_на_срок_с_причиной_и_автором()
	{
		var entry = Assert.Single(PlayerLists.ParseEntries("""
			[{"PlayerName": "Griefer", "PlayerUID": "abc=", "IssuedByPlayerName": "Admin",
			  "UntilDate": 1893456000, "Reason": "Разрушение базы", "Unknown": 1}]
			"""));

		Assert.Equal("Griefer", entry.PlayerName);
		Assert.Equal("abc=", entry.PlayerUid);
		Assert.Equal("Admin", entry.IssuedBy);
		Assert.Equal(1893456000, entry.UntilSeconds);
		Assert.Equal("Разрушение базы", entry.Reason);
		Assert.False(entry.Forever);
	}

	[Fact]
	public void Дата_строкой_ISO_тоже_срок()
	{
		var entry = Assert.Single(PlayerLists.ParseEntries("""
			[{"PlayerName": "Griefer", "UntilDate": "2030-01-01T00:00:00Z"}]
			"""));

		Assert.Equal(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), entry.UntilSeconds);
	}

	[Theory]
	[InlineData("""[{"PlayerName": "Forever"}]""")]
	[InlineData("""[{"PlayerName": "Forever", "UntilDate": "9999-12-31T23:59:59Z"}]""")]
	public void Без_срока_или_с_далёкой_датой_навсегда(string json)
	{
		Assert.True(Assert.Single(PlayerLists.ParseEntries(json)).Forever);
	}

	[Fact]
	public void Список_внутри_объекта_и_записи_без_имени()
	{
		var entries = PlayerLists.ParseEntries("""
			{"Players": [{"Name": "First"}, {"PlayerUID": "no-name"}, 42, {"PlayerName": "Second"}]}
			""");

		Assert.Equal(["First", "Second"], entries.Select(entry => entry.PlayerName));
	}

	[Fact]
	public void Нечитаемый_JSON_это_отказ_а_не_пустой_список()
	{
		Assert.ThrowsAny<JsonException>(() => PlayerLists.ParseEntries("[{\"PlayerName\": "));
	}
}
