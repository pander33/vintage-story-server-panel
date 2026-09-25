using Panel.SharedKernel;

namespace Panel.Access.Domain;

/// <summary>
/// Имя учётной записи: не пустое, не длиннее 32 символов, только буквы, цифры, дефис и
/// подчёркивание.
/// </summary>
/// <remarks>
/// Непроверенного имени не бывает: все пути (вход, проверка сессии, удаление, TOTP) получают
/// уже проверенный тип.
///
/// Буквы и цифры — в смысле Unicode (<see cref="char.IsLetterOrDigit(char)"/>), поэтому
/// «Пользователь» — допустимое имя; это закреплено тестом.
/// </remarks>
public readonly record struct UserName
{
	public const int MaxLength = 32;

	/// <summary>Учётная запись, которую нельзя ни удалить, ни понизить в правах.</summary>
	public const string AdministratorLiteral = "admin";

	private readonly string? value;

	private UserName(string value) => this.value = value;

	public string Value => value ?? throw new InvalidOperationException("Имя учётной записи не задано");

	public bool IsAdministrator => value == AdministratorLiteral;

	public static UserName Administrator { get; } = new(AdministratorLiteral);

	public static Result<UserName> Create(string? raw)
	{
		var value = raw ?? string.Empty;
		if (value.Length == 0 || value.Length > MaxLength)
		{
			return Failure.Invalid("Недопустимое имя пользователя");
		}

		foreach (var symbol in value)
		{
			if (symbol is not ('_' or '-') && !char.IsLetterOrDigit(symbol))
			{
				return Failure.Invalid("Недопустимое имя пользователя");
			}
		}

		return new UserName(value);
	}

	public static UserName Parse(string raw)
	{
		var result = Create(raw);
		return result.IsSuccess ? result.Value : throw new ArgumentException(result.Failure!.Message, nameof(raw));
	}

	public override string ToString() => value ?? string.Empty;
}
