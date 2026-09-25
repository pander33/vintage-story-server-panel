using System.Text.RegularExpressions;

namespace Panel.SharedKernel;

/// <summary>
/// Идентификатор сервера: строчная латинская буква или цифра, затем до 31 символа из того же
/// набора, дефиса и подчёркивания.
/// </summary>
/// <remarks>
/// Идентификатор попадает в имя юнита и в путь на диске, поэтому непроверенного идентификатора
/// не бывает.
/// </remarks>
public readonly partial record struct InstanceId
{
	/// <summary>Обращение к самому реестру, а не к серверу.</summary>
	public const string RegistryLiteral = "-";

	private readonly string? value;

	private InstanceId(string value) => this.value = value;

	public string Value => value ?? throw new InvalidOperationException("Идентификатор сервера не задан");

	public bool IsEmpty => value is null;

	public bool IsRegistry => value == RegistryLiteral;

	public static InstanceId Registry { get; } = new(RegistryLiteral);

	[GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,31}$")]
	private static partial Regex Pattern { get; }

	public static Result<InstanceId> Create(string? raw)
	{
		var value = (raw ?? string.Empty).Trim();
		if (value.Length == 0)
		{
			return Failure.Invalid("Идентификатор сервера пуст");
		}

		if (value == RegistryLiteral)
		{
			return Registry;
		}

		return Pattern.IsMatch(value)
			? new InstanceId(value)
			: Failure.Invalid($"Недопустимый идентификатор сервера: {value}");
	}

	/// <summary>Для констант в тестах и умолчаний; при ошибке бросает исключение.</summary>
	public static InstanceId Parse(string raw)
	{
		var result = Create(raw);
		return result.IsSuccess ? result.Value : throw new ArgumentException(result.Failure!.Message, nameof(raw));
	}

	public override string ToString() => value ?? string.Empty;
}
