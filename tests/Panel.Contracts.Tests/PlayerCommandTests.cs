using Panel.Contracts.ServerCommands;

namespace Panel.Contracts.Tests;

/// <summary>
/// Действия над игроками. Строка уходит в консоль игры, поэтому проверяется, что имя, причина
/// и объявление не могут добавить вторую команду.
/// </summary>
public sealed class PlayerCommandTests
{
	[Theory]
	[InlineData("whitelist-add", "Alice", "", "/player Alice whitelist on")]
	[InlineData("whitelist-remove", "Alice", "", "/player Alice whitelist off")]
	[InlineData("ban", "Alice", "griefing", "/ban Alice griefing")]
	[InlineData("ban", "Alice", "", "/ban Alice")]
	[InlineData("unban", "Alice", "", "/unban Alice")]
	[InlineData("kick", "Alice", " afk ", "/kick Alice afk")]
	[InlineData("op", "Alice", "", "/op Alice")]
	[InlineData("gamemode", "Alice", "creative", "/gamemode Alice creative")]
	[InlineData("announce", "", "Рестарт через 5 минут", "/announce Рестарт через 5 минут")]
	public void Команда_собирается(string action, string player, string detail, string expected) =>
		Assert.Equal(expected, PlayerCommand.Create(action, player, detail).Value.Render("suplayer"));

	/// <summary>У сервера нет <c>/deop</c>: лишение прав — это назначение роли по умолчанию.</summary>
	[Fact]
	public void Лишение_прав_назначает_роль_по_умолчанию() =>
		Assert.Equal("/player Alice role suvisitor", PlayerCommand.Create("deop", "Alice", "").Value.Render("suvisitor"));

	[Theory]
	[InlineData("teleport", "Alice", "", "Недопустимая команда")]
	[InlineData("gamemode", "Alice", "god", "Недопустимый игровой режим")]
	[InlineData("kick", "Alice Bob", "", "Имя игрока может содержать только A-Z, a-z, 0-9, _ и -")]
	[InlineData("kick", "Alice;/stop", "", "Имя игрока может содержать только A-Z, a-z, 0-9, _ и -")]
	[InlineData("kick", "Алиса", "", "Имя игрока может содержать только A-Z, a-z, 0-9, _ и -")]
	[InlineData("kick", "", "", "Имя игрока может содержать только A-Z, a-z, 0-9, _ и -")]
	[InlineData("kick", "Alice", "bye\n/stop", "Недопустимая причина")]
	[InlineData("announce", "", "hi\r/op evil", "Недопустимый текст объявления")]
	[InlineData("announce", "", "   ", "Недопустимый текст объявления")]
	public void Неверная_команда_отвергается(string action, string player, string detail, string message) =>
		Assert.Equal(message, PlayerCommand.Create(action, player, detail).Failure!.Message);

	[Fact]
	public void Слишком_длинное_имя_отвергается() =>
		Assert.False(PlayerCommand.Create("kick", new string('a', 33), "").IsSuccess);

	[Fact]
	public void Слишком_длинное_объявление_отвергается() =>
		Assert.False(PlayerCommand.Create("announce", "", new string('я', 301)).IsSuccess);

	[Theory]
	[InlineData("suplayer", true)]
	[InlineData("su-player_2", true)]
	[InlineData("admin role", false)]
	[InlineData("", false)]
	public void Роль_по_умолчанию_проверяется(string role, bool valid) =>
		Assert.Equal(valid, PlayerCommand.IsValidRole(role));
}
