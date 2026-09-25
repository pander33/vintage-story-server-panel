using Panel.SharedKernel;

namespace Panel.Fleet.Tests;

/// <summary>
/// Идентификатор сервера попадает в имя юнита systemd и в путь на диске.
/// </summary>
public sealed class InstanceIdTests
{
	[Theory]
	[InlineData("main")]
	[InlineData("a")]
	[InlineData("0")]
	[InlineData("server-2")]
	[InlineData("server_2")]
	[InlineData("abcdefghijklmnopqrstuvwxyz012345")]
	public void Допустимые_идентификаторы_принимаются(string value) =>
		Assert.True(InstanceId.Create(value).IsSuccess, value);

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("-leading")]
	[InlineData("_leading")]
	[InlineData("ВЕРХНИЙ")]
	[InlineData("Main")]
	[InlineData("with space")]
	[InlineData("../escape")]
	[InlineData("abcdefghijklmnopqrstuvwxyz0123456")]
	public void Недопустимые_идентификаторы_отвергаются(string value) =>
		Assert.False(InstanceId.Create(value).IsSuccess, value);

	[Fact]
	public void Дефис_означает_обращение_к_реестру()
	{
		var id = InstanceId.Create("-");

		Assert.True(id.IsSuccess);
		Assert.True(id.Value.IsRegistry);
	}

	[Fact]
	public void Пробелы_по_краям_обрезаются_а_не_делают_значение_недопустимым() =>
		Assert.Equal("main", InstanceId.Create("  main  ").Value.Value);
}
