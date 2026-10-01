using Panel.SharedKernel;

namespace Panel.Runtime.Tests;

public sealed class ResultTests
{
	[Fact]
	public void Неинициализированный_результат_не_выглядит_успехом()
	{
		var result = default(Result<int>);
		Assert.Throws<InvalidOperationException>(() => result.IsSuccess);
		Assert.Throws<InvalidOperationException>(() => result.Value);
		Assert.Throws<InvalidOperationException>(() => result.Failure);
		Assert.Throws<InvalidOperationException>(() => result.Map(value => value + 1));
		Assert.Throws<InvalidOperationException>(() => result.Bind(value => Result.Ok(value)));
		Assert.Throws<InvalidOperationException>(() => result.Match(value => value, _ => -1));
	}

	[Fact]
	public void Успех_может_содержать_default_значение()
	{
		Assert.Equal(0, Result.Ok(0).Value);
		Assert.True(Result.Ok<string?>(null).IsSuccess);
		Assert.Null(Result.Ok<string?>(null).Value);
		Assert.Null(Result.Ok(0).Failure);
	}

	[Fact]
	public void Отказ_сохраняется_при_преобразованиях()
	{
		var reason = Failure.Invalid("invalid");
		var result = Result.Fail<int>(reason);
		Assert.False(result.IsSuccess);
		Assert.Throws<InvalidOperationException>(() => result.Value);
		Assert.Same(reason, result.Map(value => value + 1).Failure);
		Assert.Same(reason, result.Bind(value => Result.Ok(value)).Failure);
		Assert.Same(reason, result.Match<Failure>(_ => throw new Exception(), failure => failure));
		Assert.Throws<ArgumentNullException>(() => Result.Fail<int>(null!));
	}
}
