using Panel.SharedKernel;

namespace Panel.Access.Domain;

/// <summary>
/// Одноразовый код второго фактора: ровно шесть десятичных цифр.
/// </summary>
/// <remarks>
/// Форма кода проверяется отдельно от самого кода: так пустая строка при выключенном втором
/// факторе не становится особым случаем.
/// </remarks>
public readonly record struct OneTimeCode
{
	public const int Digits = 6;

	private readonly string? value;

	private OneTimeCode(string value) => this.value = value;

	/// <summary>Код не предъявлен. Законное состояние: второй фактор может быть выключен.</summary>
	public static OneTimeCode None { get; }

	public bool IsPresent => value is not null;

	public string Value => value ?? string.Empty;

	public static Result<OneTimeCode> Create(string? raw)
	{
		var value = (raw ?? string.Empty).Trim();
		if (value.Length == 0)
		{
			return None;
		}

		return value.Length == Digits && value.All(char.IsAsciiDigit)
			? new OneTimeCode(value)
			: Failure.Invalid("Одноразовый код состоит из шести цифр");
	}

	public override string ToString() => Value;
}
