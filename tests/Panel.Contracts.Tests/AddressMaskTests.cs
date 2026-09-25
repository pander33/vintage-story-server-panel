using Panel.Contracts.Privacy;

namespace Panel.Contracts.Tests;

/// <summary>
/// Сокрытие адресов для наблюдателя.
/// </summary>
public sealed class AddressMaskTests
{
	[Theory]
	[InlineData("Alice [::1]:1234 joins.", "Alice [скрыт]:1234 joins.")]
	[InlineData("Alice [192.168.0.215]:4242 joins.", "Alice [скрыт]:4242 joins.")]
	[InlineData("Alice [2001:db8::7]:4242 joins.", "Alice [скрыт]:4242 joins.")]
	[InlineData("connect from 10.0.0.5 accepted", "connect from [скрыт] accepted")]
	[InlineData("10.0.0.5:5000", "[скрыт]:5000")]
	public void Адрес_скрывается_вместе_со_скобками(string line, string expected) =>
		Assert.Equal(expected, AddressMask.Scrub(line, hide: true));

	/// <summary>Тег мода в скобках без двоеточия — не адрес, хотя состоит из шестнадцатеричных букв.</summary>
	[Theory]
	[InlineData("[cafe] loaded")]
	[InlineData("[Server Event] Alice joins.")]
	[InlineData("version 1.20.4")]
	public void Не_адрес_остаётся(string line) =>
		Assert.Equal(line, AddressMask.Scrub(line, hide: true));

	[Fact]
	public void Без_сокрытия_строка_не_меняется() =>
		Assert.Equal("Alice [::1]:1234 joins.", AddressMask.Scrub("Alice [::1]:1234 joins.", hide: false));

	[Fact]
	public void Поле_адреса_исчезает_а_не_заменяется()
	{
		Assert.Null(AddressMask.Apply("10.0.0.5", hide: true));
		Assert.Equal("10.0.0.5", AddressMask.Apply("10.0.0.5", hide: false));
	}
}
