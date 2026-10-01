namespace Panel.SharedKernel;

/// <summary>
/// Исход операции без исключений: отказы вроде «сервер занят обслуживанием», «роль не
/// разрешает», «реестр недоступен» — обычная часть предметной области, а не ошибки программы.
/// </summary>
public readonly struct Result<T>
{
	private readonly T? value;
	private readonly Failure? failure;
	private readonly bool initialized;

	private Result(T value)
	{
		this.value = value;
		failure = null;
		initialized = true;
	}

	private Result(Failure failure)
	{
		ArgumentNullException.ThrowIfNull(failure);
		value = default;
		this.failure = failure;
		initialized = true;
	}

	public Failure? Failure
	{
		get
		{
			EnsureInitialized();
			return failure;
		}
	}

	public bool IsSuccess => Failure is null;

	public T Value => IsSuccess
		? value!
		: throw new InvalidOperationException($"Обращение к значению неуспешного результата: {Failure}");

	public static Result<T> Ok(T value) => new(value);

	public static Result<T> Fail(Failure failure) => new(failure);

	public static implicit operator Result<T>(T value) => Ok(value);

	public static implicit operator Result<T>(Failure failure) => Fail(failure);

	public Result<TNext> Map<TNext>(Func<T, TNext> map) =>
		IsSuccess ? Result<TNext>.Ok(map(value!)) : Result<TNext>.Fail(Failure!);

	public Result<TNext> Bind<TNext>(Func<T, Result<TNext>> bind) =>
		IsSuccess ? bind(value!) : Result<TNext>.Fail(Failure!);

	public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<Failure, TOut> onFailure) =>
		IsSuccess ? onSuccess(value!) : onFailure(Failure!);

	private void EnsureInitialized()
	{
		if (!initialized)
		{
			throw new InvalidOperationException("Result must be initialized with Ok or Fail.");
		}
	}
}

public static class Result
{
	public static Result<T> Ok<T>(T value) => Result<T>.Ok(value);

	public static Result<T> Fail<T>(Failure failure) => Result<T>.Fail(failure);
}
