using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Provisioning.Tests;

/// <summary>
/// Раскладка Windows: пути с обратной косой чертой, «юнит» — только имя, консоль через
/// супервизор. Разделитель выбирает раскладка, а не ОС, поэтому тест работает на любой машине.
/// </summary>
public sealed class WindowsInstanceLayoutTests
{
	private static readonly InstanceId creative = InstanceId.Parse("creative");

	private static InstanceLayout Layout(string root = @"C:\ProgramData\vs-panel\servers") =>
		InstanceLayout.Create(root, @"C:\ProgramData\vs-panel\run", creative, PlatformLayout.Windows).Value;

	private static ServerSpec Spec() => ServerSpec.FromWizard(new ServerAnswers
	{
		Id = "creative",
		Version = "1.22.7",
		Port = 42421,
		MaxPlayers = 16,
		Whitelist = false,
		Pvp = true,
		Language = "ru",
		Seed = string.Empty,
		Playstyle = "creativebuilding",
	}).Value;

	[Fact]
	public void Пути_выводятся_с_обратной_косой()
	{
		var layout = Layout(@"C:\ProgramData\vs-panel\servers\");

		Assert.Equal(@"C:\ProgramData\vs-panel\servers\creative", layout.Root);
		Assert.Equal(@"C:\ProgramData\vs-panel\servers\creative\server", layout.ProgramDirectory);
		Assert.Equal(@"C:\ProgramData\vs-panel\servers\creative\data\Saves", layout.WorldDirectory);
		Assert.Equal(@"C:\ProgramData\vs-panel\servers\creative\data\VsPanel\telemetry.json", layout.TelemetryFile);
		Assert.Equal(@"C:\ProgramData\vs-panel\servers\creative\data\Saves\default.vcdbs", layout.DefaultWorldFile);
	}

	[Fact]
	public void Юнит_только_имя_и_FIFO_нет()
	{
		var layout = Layout();

		Assert.Equal("vs-creative", layout.Unit);
		Assert.Equal(string.Empty, layout.ConsoleFifo);
		Assert.Equal(string.Empty, layout.RuntimeDirectory);
		Assert.Equal("supervised", layout.CommandTransport);
	}

	/// <summary>
	/// Сервер, созданный на Windows, получает транспорт <c>supervised</c>, а не <c>fifo</c>.
	/// </summary>
	[Fact]
	public void Запись_реестра_несёт_supervised_без_FIFO()
	{
		var entry = Layout().RegistryEntry(Spec());

		Assert.Equal("supervised", (string?) entry["command_transport"]);
		Assert.False(entry.ContainsKey("console_fifo"));
		Assert.Equal(@"C:\ProgramData\vs-panel\servers\creative\data", (string?) entry["data_dir"]);
	}

	[Fact]
	public void Косая_вперёд_сводится_к_обратной()
	{
		var layout = Layout("C:/ProgramData/vs-panel/servers");

		Assert.Equal(@"C:\ProgramData\vs-panel\servers\creative", layout.Root);
	}

	[Theory]
	[InlineData(@"servers")]
	[InlineData(@"\ProgramData\servers")]
	[InlineData(@"C:\Program Files\vs-panel")]
	[InlineData(@"C:\ProgramData\..\Windows")]
	[InlineData(@"C:\")]
	[InlineData("/srv/vs-panel")]
	public void Небезопасный_корень_отвергается(string root)
	{
		Assert.False(InstanceLayout.Create(root, @"C:\ProgramData\vs-panel\run", creative, PlatformLayout.Windows).IsSuccess);
	}

	[Fact]
	public void Путь_Windows_в_раскладке_Linux_отвергается()
	{
		Assert.False(InstanceLayout.Create(@"C:\ProgramData\vs-panel\servers", "/run", creative).IsSuccess);
	}

	[Fact]
	public void Мир_нового_сервера_указывает_в_каталог_Windows()
	{
		var generated = new System.Text.Json.Nodes.JsonObject { ["WorldConfig"] = new System.Text.Json.Nodes.JsonObject() };

		var applied = InitialConfig.Apply(generated, Spec(), Layout());

		Assert.Equal(
			@"C:\ProgramData\vs-panel\servers\creative\data\Saves\default.vcdbs",
			(string?) applied["WorldConfig"]!["SaveFileLocation"]);
	}
}
