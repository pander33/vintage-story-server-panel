namespace Panel.SharedKernel;

/// <summary>
/// Отказ как значение. Код нужен для ветвления и HTTP-статуса, сообщение показывается в
/// интерфейсе и поэтому пишется по-русски.
/// </summary>
public sealed record Failure(FailureCode Code, string Message)
{
	public static Failure NotFound(string message) => new(FailureCode.NotFound, message);
	public static Failure Invalid(string message) => new(FailureCode.Invalid, message);
	public static Failure Forbidden(string message) => new(FailureCode.Forbidden, message);
	public static Failure Unauthenticated(string message) => new(FailureCode.Unauthenticated, message);
	public static Failure Conflict(string message) => new(FailureCode.Conflict, message);
	public static Failure Unavailable(string message) => new(FailureCode.Unavailable, message);

	/// <summary>Загрузка больше предела: 413.</summary>
	public static Failure TooLarge(string message) => new(FailureCode.TooLarge, message);

	public override string ToString() => $"{Code}: {Message}";
}

public enum FailureCode
{
	Invalid,
	NotFound,
	Unauthenticated,
	Forbidden,
	Conflict,
	Unavailable,
	TooLarge,
}
