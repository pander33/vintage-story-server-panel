using Panel.Contracts.ServerCommands;

namespace Panel.Contracts.Tests;

/// <summary>
/// Команда живой конфигурации мира. Строка уходит в консоль игры, поэтому проверяются и
/// попытки обойти правила.
/// </summary>
public sealed class WorldConfigCommandTests
{
	[Theory]
	[InlineData("set", "bool", "allowMap", "true", "/worldconfig allowMap true")]
	[InlineData("set", "int", "daysPerMonth", "-12", "/worldconfig daysPerMonth -12")]
	[InlineData("set", "double", "snowAccum", ".5", "/worldconfig snowAccum .5")]
	[InlineData("create", "string", "customKey", "  hello world  ", "/worldconfigcreate string customKey hello world")]
	[InlineData("create", "string", "empty", "", "/worldconfigcreate string empty ")]
	public void Верная_команда_собирается(string action, string type, string name, string value, string expected) =>
		Assert.Equal(expected, WorldConfigCommand.Create(action, type, name, value).Value.Render());

	[Theory]
	[InlineData("delete", "bool", "a", "true")]
	[InlineData("set", "long", "a", "1")]
	[InlineData("set", "bool", "1abc", "true")]
	[InlineData("set", "bool", "a b", "true")]
	[InlineData("set", "bool", "a;b", "true")]
	[InlineData("set", "bool", "allowMap", "yes")]
	[InlineData("set", "int", "a", "1.5")]
	[InlineData("set", "int", "a", "1234567890123456789")]
	[InlineData("set", "double", "a", "1,5")]
	[InlineData("set", "double", "a", "1e5")]
	[InlineData("set", "int", "a", "")]
	[InlineData(null, null, null, null)]
	public void Неверная_команда_отвергается(string? action, string? type, string? name, string? value) =>
		Assert.False(WorldConfigCommand.Create(action, type, name, value).IsSuccess);

	/// <summary>
	/// Перевод строки в значении дал бы вторую команду в консоли и должен отвергаться.
	/// </summary>
	[Theory]
	[InlineData("x\n/op attacker")]
	[InlineData("x\r/stop")]
	[InlineData("x\u0000")]
	public void Управляющий_символ_в_значении_отвергается(string value) =>
		Assert.False(WorldConfigCommand.Create("create", "string", "note", value).IsSuccess);

	/// <summary>
	/// Только цифры ASCII: другие цифры игра разобрала бы не так, как ждёт администратор.
	/// </summary>
	[Fact]
	public void Только_ascii_цифры() =>
		Assert.False(WorldConfigCommand.Create("set", "int", "a", "١٢").IsSuccess);

	/// <summary>Значение передаётся без пробелов по краям, но с исходным регистром: TRUE игра разберёт сама.</summary>
	[Fact]
	public void Аргументы_демона_в_фиксированном_порядке() =>
		Assert.Equal(
			["set", "bool", "allowMap", "TRUE"],
			WorldConfigCommand.Create("set", "bool", "allowMap", " TRUE ").Value.ToArguments());
}
