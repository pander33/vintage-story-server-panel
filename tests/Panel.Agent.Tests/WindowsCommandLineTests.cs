using Panel.Agent.Supervision;

namespace Panel.Agent.Tests;

/// <summary>
/// Сборка командной строки для <c>CreateProcessW</c>, обратная правилам
/// <c>CommandLineToArgvW</c>.
/// </summary>
public sealed class WindowsCommandLineTests
{
	[Theory]
	[InlineData("VintagestoryServer.dll", "VintagestoryServer.dll")]
	[InlineData("--dataPath", "--dataPath")]
	[InlineData(@"C:\ProgramData\vs-panel\servers\main\data", @"C:\ProgramData\vs-panel\servers\main\data")]
	[InlineData("", "\"\"")]
	[InlineData(@"C:\Program Files\dotnet\dotnet.exe", "\"C:\\Program Files\\dotnet\\dotnet.exe\"")]
	[InlineData(@"C:\Program Files\x\", "\"C:\\Program Files\\x\\\\\"")]
	[InlineData("a\"b", "\"a\\\"b\"")]
	[InlineData(@"a\""b", "\"a\\\\\\\"b\"")]
	[InlineData(@"a\\b c", "\"a\\\\b c\"")]
	public void Аргумент_экранируется_по_правилам_CommandLineToArgvW(string argument, string expected) =>
		Assert.Equal(expected, WindowsCommandLine.Quote(argument));

	[Fact]
	public void Аргументы_разделяются_одним_пробелом()
	{
		var line = WindowsCommandLine.Build([@"C:\Program Files\dotnet\dotnet.exe", "Server.dll", "--dataPath", @"D:\vs data"]);

		Assert.Equal("\"C:\\Program Files\\dotnet\\dotnet.exe\" Server.dll --dataPath \"D:\\vs data\"", line);
	}

	[Fact]
	public void Нулевой_символ_отвергается() =>
		Assert.Throws<ArgumentException>(() => WindowsCommandLine.Quote("a\0b"));
}
