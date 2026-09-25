namespace Panel.SharedKernel;

/// <summary>
/// Успех без значения. Нужен, чтобы операция без результата сообщала об отказе так же, как
/// операция с результатом: через <c>Result&lt;Done&gt;</c>, а не исключением или <c>bool</c> без
/// причины.
/// </summary>
public readonly record struct Done
{
	public static Done Value { get; }

	public static Result<Done> Ok { get; } = Result.Ok(Value);
}
