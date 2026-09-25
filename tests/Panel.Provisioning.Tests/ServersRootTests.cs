using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Provisioning.Tests;

/// <summary>
/// Каталог серверов, выбранный оператором: путь по правилу раскладки, не системный и не
/// пересекающийся со служебными каталогами панели.
/// </summary>
public sealed class ServersRootTests
{
	private static readonly string[] windowsExclusive =
		[@"C:\ProgramData\vs-panel\run", @"C:\ProgramData\vs-panel\state\staging", @"C:\ProgramData\vs-panel\telemetry"];

	private static readonly string[] windowsContainers = [@"C:\ProgramData\vs-panel"];

	private static readonly string[] linuxExclusive = ["/run", "/var/lib/vs-panel/staging", "/usr/local/share/vs-panel/telemetry"];

	private static readonly string[] linuxContainers = ["/etc/vs-panel", "/etc"];

	private static Result<ServersRoot> Windows(string path) =>
		ServersRoot.Create(path, PlatformLayout.Windows, windowsExclusive, windowsContainers);

	private static Result<ServersRoot> Linux(string path) =>
		ServersRoot.Create(path, PlatformLayout.Linux, linuxExclusive, linuxContainers);

	[Theory]
	[InlineData(@"D:\vs-servers", @"D:\vs-servers")]
	[InlineData(@"d:/Games/VS/", @"d:\Games\VS")]
	[InlineData(@"C:\ProgramData\vs-panel\servers", @"C:\ProgramData\vs-panel\servers")]
	[InlineData(@"E:\vintage_story.servers", @"E:\vintage_story.servers")]
	public void Путь_Windows_приводится_к_обратной_косой(string raw, string expected)
	{
		var root = Windows(raw);

		Assert.True(root.IsSuccess, root.Failure?.Message);
		Assert.Equal(expected, root.Value.Path);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData(@"D:\Vintage Story")]
	[InlineData(@"vs-servers")]
	[InlineData(@"D:\")]
	[InlineData(@"\\server\share\vs")]
	[InlineData(@"D:\vs\..\Windows")]
	[InlineData(@"D:\сервера")]
	public void Небезопасный_путь_Windows_отвергается(string raw)
	{
		var root = Windows(raw);

		Assert.False(root.IsSuccess);
		Assert.Equal(FailureCode.Invalid, root.Failure!.Code);
	}

	[Theory]
	[InlineData(@"C:\Windows\vs")]
	[InlineData(@"C:\windows")]
	[InlineData(@"C:\Users\admin\vs")]
	[InlineData(@"C:\ProgramData")]
	[InlineData(@"D:\Recovery")]
	public void Системный_каталог_Windows_не_годится(string raw)
	{
		var root = Windows(raw);

		Assert.False(root.IsSuccess);
		Assert.Contains("системный каталог", root.Failure!.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(@"C:\ProgramData\vs-panel\run")]
	[InlineData(@"C:\ProgramData\vs-panel\run\servers")]
	[InlineData(@"C:\ProgramData\vs-panel\state")]
	[InlineData(@"C:\ProgramData\vs-panel\TELEMETRY")]
	public void Служебный_каталог_панели_не_годится(string raw)
	{
		var root = Windows(raw);

		Assert.False(root.IsSuccess);
		Assert.Contains("служебным каталогом", root.Failure!.Message, StringComparison.Ordinal);
	}

	/// <summary>Сам каталог данных панели не подходит: серверы оказались бы рядом с её состоянием.</summary>
	[Fact]
	public void Каталог_данных_панели_годится_только_изнутри()
	{
		Assert.False(Windows(@"C:\ProgramData\vs-panel").IsSuccess);
		Assert.True(Windows(@"C:\ProgramData\vs-panel\games").IsSuccess);
	}

	[Theory]
	[InlineData("/srv/vs-panel")]
	[InlineData("/data/vs-servers/")]
	[InlineData("/home/games/vs")]
	[InlineData("/var/lib/vintagestory")]
	public void Путь_Linux_принимается(string raw)
	{
		var root = Linux(raw);

		Assert.True(root.IsSuccess, root.Failure?.Message);
		Assert.Equal(raw.TrimEnd('/'), root.Value.Path);
	}

	[Theory]
	[InlineData("/")]
	[InlineData("/srv")]
	[InlineData("/home")]
	[InlineData("/etc/vs")]
	[InlineData("/usr/local/vs")]
	[InlineData("/tmp/vs")]
	[InlineData("/run/vs")]
	[InlineData("/var/lib/vs-panel/servers")]
	[InlineData("/opt/vs-panel/servers")]
	[InlineData("data/vs")]
	[InlineData("/data/my servers")]
	public void Системный_или_небезопасный_путь_Linux_отвергается(string raw)
	{
		Assert.False(Linux(raw).IsSuccess);
	}

	[Fact]
	public void Каталог_не_может_вмещать_служебный()
	{
		var root = ServersRoot.Create("/data", PlatformLayout.Linux, ["/data/panel/run"], []);

		Assert.False(root.IsSuccess);
		Assert.Contains("служебным каталогом", root.Failure!.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Регистр_букв_на_Windows_не_делает_каталог_другим()
	{
		var root = Windows(@"D:\VS-Servers").Value;

		Assert.True(root.Is(ServersRoot.FromConfiguration(@"d:\vs-servers\", PlatformLayout.Windows)));
		Assert.False(root.Is(ServersRoot.FromConfiguration(@"D:\vs-servers2", PlatformLayout.Windows)));
		Assert.False(root.Is(null));
	}

	[Fact]
	public void Регистр_букв_на_Linux_делает_каталог_другим()
	{
		var root = Linux("/srv/VS").Value;

		Assert.False(root.Is(ServersRoot.FromConfiguration("/srv/vs", PlatformLayout.Linux)));
	}

	[Theory]
	[InlineData(@"D:\vs-servers", @"D:\")]
	[InlineData(@"D:\games\vs", @"D:\games")]
	public void Родитель_Windows(string raw, string parent)
	{
		Assert.Equal(parent, Windows(raw).Value.Parent);
	}

	[Theory]
	[InlineData("/srv/vs-panel", "/srv")]
	[InlineData("/data", "/")]
	public void Родитель_Linux(string raw, string parent)
	{
		Assert.Equal(parent, Linux(raw).Value.Parent);
	}
}
