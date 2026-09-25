using Panel.Observability.Domain;

namespace Panel.Observability.Tests;

/// <summary>Лента игровых событий.</summary>
public sealed class GameEventTests
{
	[Theory]
	[InlineData("Fall/Gravity", "падение")]
	[InlineData("Player/SlashingAttack", "другой игрок")]
	[InlineData("Lava", "Lava")]
	[InlineData("", "неизвестно")]
	public void Причина_смерти_по_источнику(string text, string expected) =>
		Assert.Equal(expected, Event("death", "Alice", text).Detail);

	[Fact]
	public void У_чата_причины_нет() =>
		Assert.Null(Event("chat", "Alice", "hi").Detail);

	/// <summary><c>seq</c> обнуляется при каждом старте сервера, поэтому в ключ входят время и игрок.</summary>
	[Fact]
	public void Ключ_события() =>
		Assert.Equal("100:7:chat:Alice", (Event("chat", "Alice", "hi") with { At = 100, Seq = 7 }).Key);

	[Fact]
	public void Наблюдатель_не_видит_uid() =>
		Assert.Equal(string.Empty, Event("chat", "Alice", "hi").Masked().Uid);

	[Theory]
	[InlineData("all", "", 3)]
	[InlineData("chat", "", 2)]
	[InlineData("all", "ALICE", 2)]
	[InlineData("all", "привет", 1)]
	public void Фильтр_по_виду_тексту_и_игроку(string kind, string search, int expected)
	{
		var events = new[]
		{
			Event("chat", "Alice", "Привет всем"),
			Event("chat", "Bob", "hi alice"),
			Event("join", "Carol", string.Empty),
		};

		Assert.Equal(expected, GameEventFilter.Create(kind, search).Value.Apply(events).Count);
	}

	[Fact]
	public void Неизвестный_вид_отвергается() =>
		Assert.Equal("Недопустимый тип события", GameEventFilter.Create("trade", "").Failure!.Message);

	private static GameEvent Event(string kind, string player, string text) =>
		new(1, 1, kind, player, "uid-123", null, text);
}
