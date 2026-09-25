using Panel.SharedKernel;

namespace Panel.Contracts.ServerCommands;

/// <summary>
/// Произвольная строка для консоли игры (ADR-17).
/// </summary>
/// <remarks>
/// <para>
/// Эта строка не проходит через каталог действий, поэтому право на неё есть только у
/// администратора. Строка может быть любой командой игры, но только одной: перевод строки
/// стал бы второй командой, а управляющий символ — вводом, которого не видно в поле.
/// </para>
/// <para>
/// В оболочку ОС строка не попадает: демон пишет её в stdin или FIFO игры. Правило лежит в
/// контрактах, потому что им пользуются обе стороны, и демон проверяет строку заново.
/// </para>
/// </remarks>
public sealed record RawConsoleLine
{
	public const int MaxLength = 500;

	private RawConsoleLine(string text) => Text = text;

	public string Text { get; }

	public static Result<RawConsoleLine> Create(string? raw)
	{
		var text = raw?.Trim() ?? string.Empty;
		if (text.Length == 0)
		{
			return Failure.Invalid("Введите команду");
		}

		if (text.Length > MaxLength)
		{
			return Failure.Invalid($"Команда длиннее {MaxLength} символов");
		}

		if (text.Any(char.IsControl))
		{
			return Failure.Invalid("Команда должна быть одной строкой без управляющих символов");
		}

		return new RawConsoleLine(text);
	}

	public override string ToString() => Text;
}
