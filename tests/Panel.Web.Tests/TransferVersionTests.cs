using Panel.Web.Application;

namespace Panel.Web.Tests;

public sealed class TransferVersionTests
{
	[Theory]
	[InlineData("1.22.7", "1.22.7", false)]
	[InlineData("", "1.22.7", false)]
	[InlineData("1.22.7", "не версия", false)]
	[InlineData("1.21.3", "1.22.0", true)]
	public void Допустимые_сочетания_проходят(string packaged, string chosen, bool accept) =>
		Assert.Null(TransferVersion.Check(packaged, chosen, accept));

	[Fact]
	public void Более_старая_версия_отказ_даже_с_согласием() =>
		Assert.NotNull(TransferVersion.Check("1.22.7", "1.21.3", acceptUpgrade: true));

	[Fact]
	public void Более_новая_версия_требует_согласия()
	{
		Assert.NotNull(TransferVersion.Check("1.21.3", "1.22.0", acceptUpgrade: false));
		Assert.Null(TransferVersion.Check("1.21.3", "1.22.0", acceptUpgrade: true));
	}
}
