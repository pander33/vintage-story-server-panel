using Panel.Contracts.ServerCommands;

namespace Panel.Contracts.Tests;

/// <summary>
/// Произвольная строка консоли (ADR-17): команда может быть любой, но одной. Всё, что
/// добавило бы вторую команду или невидимый ввод, отвергается.
/// </summary>
public sealed class RawConsoleLineTests
{
	[Theory]
	[InlineData("/stats", "/stats")]
	[InlineData("  /say Привет, мир  ", "/say Привет, мир")]
	[InlineData("/worldconfig temporalStorms off", "/worldconfig temporalStorms off")]
	public void Строка_принимается_без_краевых_пробелов(string raw, string expected) =>
		Assert.Equal(expected, RawConsoleLine.Create(raw).Value.Text);

	[Theory]
	[InlineData(null, "Введите команду")]
	[InlineData("", "Введите команду")]
	[InlineData("   ", "Введите команду")]
	[InlineData("/say hi\n/stop", "Команда должна быть одной строкой без управляющих символов")]
	[InlineData("/say hi\r/op evil", "Команда должна быть одной строкой без управляющих символов")]
	[InlineData("/say \u001b[2Jhi", "Команда должна быть одной строкой без управляющих символов")]
	[InlineData("/say \u0000", "Команда должна быть одной строкой без управляющих символов")]
	public void Неверная_строка_отвергается(string? raw, string message) =>
		Assert.Equal(message, RawConsoleLine.Create(raw).Failure!.Message);

	[Fact]
	public void Длина_ограничена()
	{
		Assert.True(RawConsoleLine.Create("/" + new string('a', RawConsoleLine.MaxLength - 1)).IsSuccess);
		Assert.Equal(
			"Команда длиннее 500 символов",
			RawConsoleLine.Create("/" + new string('a', RawConsoleLine.MaxLength)).Failure!.Message);
	}
}
