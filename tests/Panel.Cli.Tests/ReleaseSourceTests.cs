using System.Runtime.InteropServices;
using Panel.Cli;

namespace Panel.Cli.Tests;

/// <summary>
/// Установка из релиза GitHub: какой архив, какой тег и какая сумма. Сеть здесь не нужна —
/// проверяется только то, что решается до загрузки и после неё.
/// </summary>
public sealed class ReleaseSourceTests
{
	[Theory]
	[InlineData(false, Architecture.X64, "linux-x64")]
	[InlineData(false, Architecture.Arm64, "linux-arm64")]
	[InlineData(true, Architecture.X64, "win-x64")]
	public void Цель_машины(bool windows, Architecture architecture, string rid) =>
		Assert.Equal(rid, ReleaseSource.Rid(windows, architecture));

	[Fact]
	public void Для_32_битной_машины_пакета_нет()
	{
		Assert.Null(ReleaseSource.Rid(false, Architecture.X86));
		Assert.Null(ReleaseSource.Rid(true, Architecture.Arm));
	}

	/// <summary>Установщик сверяет цель пакета с машиной и откажет win-x64 на ARM64.</summary>
	[Fact]
	public void Для_Windows_на_ARM64_пакета_нет() =>
		Assert.Null(ReleaseSource.Rid(true, Architecture.Arm64));

	[Theory]
	[InlineData("0.10.0", "0.9.0")]
	[InlineData("v0.3.0", "0.2.9")]
	[InlineData("0.3.0", "0.3.0-rc1")]
	[InlineData("0.3.0-rc2", "0.3.0-rc1")]
	[InlineData("0.3.0-rc1", "0.2.0")]
	public void Новая_версия_старше(string newer, string older)
	{
		Assert.True(ReleaseSource.CompareVersions(newer, older) > 0);
		Assert.True(ReleaseSource.CompareVersions(older, newer) < 0);
	}

	[Fact]
	public void Тег_и_версия_равны() =>
		Assert.Equal(0, ReleaseSource.CompareVersions("v0.3.0", "0.3.0"));

	[Fact]
	public void Архив_Linux_tar_gz_Windows_zip()
	{
		Assert.Equal("vs-panel-linux-arm64.tar.gz", ReleaseSource.PackageName("linux-arm64"));
		Assert.Equal("vs-panel-win-x64.zip", ReleaseSource.PackageName("win-x64"));
	}

	[Theory]
	[InlineData("0.3.0", "v0.3.0")]
	[InlineData("v0.3.0", "v0.3.0")]
	[InlineData(" 0.3.0-rc1 ", "v0.3.0-rc1")]
	public void Версия_и_тег_называют_один_релиз(string version, string tag) =>
		Assert.Equal(tag, ReleaseSource.Tag(version));

	[Theory]
	[InlineData("")]
	[InlineData("../0.3.0")]
	[InlineData("0.3.0/x")]
	[InlineData("0.3 .0")]
	[InlineData("0.3.0+build")]
	public void Тег_не_выходит_за_адрес_релиза(string version) =>
		Assert.Null(ReleaseSource.Tag(version));

	[Fact]
	public void Адрес_архива_берётся_из_выбранного_тега()
	{
		var uri = ReleaseSource.AssetUri("v0.3.0", "vs-panel-linux-x64.tar.gz");

		Assert.Equal($"https://github.com/{ReleaseSource.Repository}/releases/download/v0.3.0/vs-panel-linux-x64.tar.gz", uri.AbsoluteUri);
	}

	[Fact]
	public void Последний_релиз_из_ответа_GitHub()
	{
		Assert.Equal("v0.3.0", ReleaseSource.ParseLatestTag("""{"id": 1, "tag_name": "v0.3.0", "name": "0.3.0"}"""));
		Assert.Null(ReleaseSource.ParseLatestTag("""{"message": "Not Found"}"""));
		Assert.Null(ReleaseSource.ParseLatestTag("""{"tag_name": "../../evil"}"""));
		Assert.Null(ReleaseSource.ParseLatestTag("not json"));
	}

	[Fact]
	public void Сумма_в_формате_sha256sum()
	{
		var linux = new string('a', 64);
		var windows = new string('B', 64);
		var sums = $"{linux}  vs-panel-linux-x64.tar.gz\n{windows} *vs-panel-win-x64.zip\r\n";

		Assert.Equal(linux, ReleaseSource.ExpectedHash(sums, "vs-panel-linux-x64.tar.gz"));
		Assert.Equal(new string('b', 64), ReleaseSource.ExpectedHash(sums, "vs-panel-win-x64.zip"));
		Assert.Null(ReleaseSource.ExpectedHash(sums, "vs-panel-linux-arm64.tar.gz"));
		Assert.Null(ReleaseSource.ExpectedHash("abc  vs-panel-linux-x64.tar.gz", "vs-panel-linux-x64.tar.gz"));
	}

	[Fact]
	public void Версия_релиза_задаётся_параметром_или_переменной()
	{
		var byArgument = InstallOptions.Parse(["--version", "0.3.0"], new Dictionary<string, string>(), InstallPaths.System);
		var byVariable = InstallOptions.ParseWindows([], new Dictionary<string, string> { ["VS_PANEL_VERSION"] = "v0.3.1" });

		Assert.Equal("v0.3.0", byArgument.Value.Release);
		Assert.Equal("v0.3.1", byVariable.Value.Release);
		Assert.Empty(InstallOptions.Parse([], new Dictionary<string, string>(), InstallPaths.System).Value.Release);
	}

	[Fact]
	public void Каталог_сборки_и_релиз_исключают_друг_друга()
	{
		Assert.False(InstallOptions.Parse(["--from", "/tmp/build", "--version", "0.3.0"], new Dictionary<string, string>(), InstallPaths.System).IsSuccess);
		Assert.False(InstallOptions.Parse(["--version", "../x"], new Dictionary<string, string>(), InstallPaths.System).IsSuccess);
	}

	/// <summary>
	/// get.sh и get.ps1 выбирают релиз по VS_PANEL_VERSION и запускают установщик с --from из
	/// скачанного пакета; переменная всё ещё в окружении и не должна ему мешать.
	/// </summary>
	[Fact]
	public void Переменная_версии_не_мешает_установке_из_каталога()
	{
		var environment = new Dictionary<string, string> { ["VS_PANEL_VERSION"] = "0.3.0" };

		var linux = InstallOptions.Parse(["--from", "/tmp/build"], environment, InstallPaths.System);
		var windows = InstallOptions.ParseWindows(["--from", @"C:uild"], environment);

		Assert.True(linux.IsSuccess);
		Assert.Empty(linux.Value.Release);
		Assert.True(windows.IsSuccess);
		Assert.Empty(windows.Value.Release);
	}
}
