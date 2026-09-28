using System.Runtime.CompilerServices;

namespace Panel.SharedKernel;

/// <summary>
/// Отказ как значение. Код нужен для ветвления и HTTP-статуса, текст показывается в интерфейсе:
/// русский шаблон, который граница HTTP переводит на язык запроса (<see cref="Phrase"/>).
/// </summary>
/// <remarks>
/// У каждой фабрики три перегрузки: интерполированная строка сохраняет шаблон через
/// <see cref="PhraseHandler"/>, готовый текст и <see cref="Phrase"/> идут как есть. Приоритет у
/// первой: иначе строка с одними константами (<c>$"… {Const} …"</c>) была бы неоднозначной.
/// </remarks>
public sealed record Failure(FailureCode Code, Phrase Text)
{
	/// <summary>Русский текст: для журналов, файлов состояния и сравнения в тестах.</summary>
	public string Message => Text.ToString();

	public static Failure NotFound(string message) => NotFound(Phrase.From(message));
	public static Failure NotFound(Phrase message) => new(FailureCode.NotFound, message);
	[OverloadResolutionPriority(1)]
	public static Failure NotFound(ref PhraseHandler message) => new(FailureCode.NotFound, message.ToPhrase());

	public static Failure Invalid(string message) => Invalid(Phrase.From(message));
	public static Failure Invalid(Phrase message) => new(FailureCode.Invalid, message);
	[OverloadResolutionPriority(1)]
	public static Failure Invalid(ref PhraseHandler message) => new(FailureCode.Invalid, message.ToPhrase());

	public static Failure Forbidden(string message) => Forbidden(Phrase.From(message));
	public static Failure Forbidden(Phrase message) => new(FailureCode.Forbidden, message);
	[OverloadResolutionPriority(1)]
	public static Failure Forbidden(ref PhraseHandler message) => new(FailureCode.Forbidden, message.ToPhrase());

	public static Failure Unauthenticated(string message) => Unauthenticated(Phrase.From(message));
	public static Failure Unauthenticated(Phrase message) => new(FailureCode.Unauthenticated, message);
	[OverloadResolutionPriority(1)]
	public static Failure Unauthenticated(ref PhraseHandler message) => new(FailureCode.Unauthenticated, message.ToPhrase());

	public static Failure Conflict(string message) => Conflict(Phrase.From(message));
	public static Failure Conflict(Phrase message) => new(FailureCode.Conflict, message);
	[OverloadResolutionPriority(1)]
	public static Failure Conflict(ref PhraseHandler message) => new(FailureCode.Conflict, message.ToPhrase());

	public static Failure Unavailable(string message) => Unavailable(Phrase.From(message));
	public static Failure Unavailable(Phrase message) => new(FailureCode.Unavailable, message);
	[OverloadResolutionPriority(1)]
	public static Failure Unavailable(ref PhraseHandler message) => new(FailureCode.Unavailable, message.ToPhrase());

	/// <summary>Загрузка больше предела: 413.</summary>
	public static Failure TooLarge(string message) => TooLarge(Phrase.From(message));
	public static Failure TooLarge(Phrase message) => new(FailureCode.TooLarge, message);
	[OverloadResolutionPriority(1)]
	public static Failure TooLarge(ref PhraseHandler message) => new(FailureCode.TooLarge, message.ToPhrase());

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
