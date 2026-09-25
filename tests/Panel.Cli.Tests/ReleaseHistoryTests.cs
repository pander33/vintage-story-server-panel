using Panel.Cli;

namespace Panel.Cli.Tests;

/// <summary>
/// Порядок активаций определяет, куда откатываться и что удалять: хранятся текущая и две
/// предыдущие версии (§3 заметки «Установщик и поставка .NET-панели»).
/// </summary>
public sealed class ReleaseHistoryTests
{
	[Fact]
	public void Повторная_активация_переносит_версию_в_конец()
	{
		var history = ReleaseHistory.Parse("0.2.0\n0.2.1\n").Activate("0.2.0");

		Assert.Equal(["0.2.1", "0.2.0"], history.Activations);
		Assert.Equal("0.2.1\n0.2.0\n", history.Serialize());
	}

	[Fact]
	public void Откат_идёт_на_последнюю_установленную_версию_до_текущей()
	{
		var history = ReleaseHistory.Parse("0.2.0\n0.2.1\n0.2.2\n");

		Assert.Equal("0.2.1", history.Previous("0.2.2", ["0.2.0", "0.2.1", "0.2.2"]));
		Assert.Equal("0.2.0", history.Previous("0.2.2", ["0.2.0", "0.2.2"]));
		Assert.Null(history.Previous("0.2.2", ["0.2.2"]));
	}

	/// <summary>После отката предыдущей становится версия, с которой откатились.</summary>
	[Fact]
	public void После_отката_предыдущей_становится_оставленная_версия()
	{
		var history = ReleaseHistory.Parse("0.2.0\n0.2.1\n").Activate("0.2.0");

		Assert.Equal("0.2.1", history.Previous("0.2.0", ["0.2.0", "0.2.1"]));
	}

	[Fact]
	public void Хранятся_текущая_и_две_предыдущие()
	{
		var history = ReleaseHistory.Parse("0.1.0\n0.2.0\n0.2.1\n0.2.2\n0.2.3\n");

		var obsolete = history.Obsolete(["0.1.0", "0.2.0", "0.2.1", "0.2.2", "0.2.3"], "0.2.3");

		Assert.Equal(["0.1.0", "0.2.0"], obsolete);
	}

	[Fact]
	public void Версия_без_записи_в_журнале_считается_самой_старой()
	{
		var history = ReleaseHistory.Parse("0.2.1\n0.2.2\n0.2.3\n");

		Assert.Equal(["0.2.0-alpha"], history.Obsolete(["0.2.0-alpha", "0.2.1", "0.2.2", "0.2.3"], "0.2.3"));
	}

	[Fact]
	public void Журнал_без_повторов_и_мусора()
	{
		var history = ReleaseHistory.Parse("0.2.0\r\n../etc\n\n0.2.1\n0.2.0\n");

		Assert.Equal(["0.2.1", "0.2.0"], history.Activations);
	}

	[Theory]
	[InlineData("0.2.0")]
	[InlineData("0.2.0-alpha")]
	[InlineData("0.2.0+build.5")]
	public void Годные_имена_версий(string version) => Assert.True(ReleaseHistory.IsVersion(version));

	[Theory]
	[InlineData("")]
	[InlineData("..")]
	[InlineData("../0.2.0")]
	[InlineData("0.2/0")]
	[InlineData("-0.2.0")]
	[InlineData("0.2 0")]
	public void Имя_версии_не_выходит_за_каталог(string version)
	{
		Assert.False(ReleaseHistory.IsVersion(version));
		Assert.Throws<ArgumentException>(() => ReleaseHistory.Parse(null).Activate(version));
	}
}
