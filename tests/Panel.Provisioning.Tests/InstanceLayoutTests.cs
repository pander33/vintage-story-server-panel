using System.Text.Json.Nodes;
using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Provisioning.Tests;

/// <summary>Пути, юнит и начальные настройки нового сервера, которые выводит демон.</summary>
public sealed class InstanceLayoutTests
{
	private static readonly InstanceId creative = InstanceId.Parse("creative");

	private static InstanceLayout Layout() => InstanceLayout.Create("/srv/vs-panel/", "/run", creative).Value;

	private static ServerSpec Spec(bool whitelist = false, string seed = "") => ServerSpec.FromWizard(new ServerAnswers
	{
		Id = "creative",
		Version = "1.22.7",
		Port = 42421,
		MaxPlayers = 16,
		Whitelist = whitelist,
		Pvp = true,
		Language = "ru",
		Seed = seed,
		Playstyle = "creativebuilding",
	}).Value;

	[Fact]
	public void Все_пути_выводятся_из_идентификатора()
	{
		var layout = Layout();

		Assert.Equal("/srv/vs-panel/creative", layout.Root);
		Assert.Equal("/srv/vs-panel/creative/server", layout.ProgramDirectory);
		Assert.Equal("/srv/vs-panel/creative/data/Saves", layout.WorldDirectory);
		Assert.Equal("/srv/vs-panel/creative/backups", layout.BackupDirectory);
		Assert.Equal("/srv/vs-panel/creative/data/VsPanel/telemetry.json", layout.TelemetryFile);
		Assert.Equal("vs-creative.service", layout.Unit);
		Assert.Equal("/run/vs-creative/console.fifo", layout.ConsoleFifo);
	}

	[Theory]
	[InlineData("srv/vs-panel")]
	[InlineData("/srv/vs panel")]
	[InlineData("/srv/../etc")]
	[InlineData("/srv/vs-panel\nExecStart=/bin/sh")]
	[InlineData("")]
	public void Небезопасный_корень_не_принимается(string root)
	{
		Assert.False(InstanceLayout.Create(root, "/run", creative).IsSuccess);
	}

	[Fact]
	public void Реестру_нельзя_адресовать_раскладку()
	{
		Assert.False(InstanceLayout.Create("/srv/vs-panel", "/run", InstanceId.Registry).IsSuccess);
	}

	[Fact]
	public void Запись_реестра_с_фиксированным_порядком_ключей()
	{
		var entry = Layout().RegistryEntry(Spec());

		Assert.Equal(
			["name", "unit", "port", "game_version", "data_dir", "mods_dir", "backup_dir", "config_file", "world_dir", "logs_dir", "telemetry_file", "game_events_file", "mod_config_dir", "program_dir", "command_transport", "console_fifo"],
			entry.Select(pair => pair.Key));
		Assert.Equal("fifo", (string?) entry["command_transport"]);
		Assert.Equal(42421, (int) entry["port"]!);
	}

	[Fact]
	public void Юнит_собирается_только_из_выведенных_значений()
	{
		var unit = GameUnit.Create(Layout(), "/usr/bin/dotnet", "/usr/local/libexec/vs-console-wrapper").Value.Text;

		Assert.Contains("User=vsgame\nGroup=vsgame\n", unit, StringComparison.Ordinal);
		Assert.Contains("RuntimeDirectory=vs-creative\n", unit, StringComparison.Ordinal);
		Assert.Contains(
			"ExecStart=/usr/local/libexec/vs-console-wrapper /run/vs-creative/console.fifo /usr/bin/dotnet /srv/vs-panel/creative/server/VintagestoryServer.dll --dataPath /srv/vs-panel/creative/data --append --archiveLogFileCount 14 --archiveLogFileMaxSizeMb 1024\n",
			unit,
			StringComparison.Ordinal);
		Assert.DoesNotContain("\r", unit, StringComparison.Ordinal);
		Assert.EndsWith("WantedBy=multi-user.target\n", unit, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("/usr/bin/dotnet --evil")]
	[InlineData("dotnet")]
	[InlineData("/opt/my dotnet/dotnet")]
	public void Путь_с_пробелом_не_попадает_в_ExecStart(string dotnet)
	{
		Assert.False(GameUnit.Create(Layout(), dotnet, "/usr/local/libexec/vs-console-wrapper").IsSuccess);
	}

	[Fact]
	public void Значения_панели_кладутся_поверх_сгенерированной_игрой_конфигурации()
	{
		var generated = new JsonObject
		{
			["ServerName"] = "Vintage Story Server",
			["Roles"] = new JsonArray(new JsonObject { ["Code"] = "suplayer" }),
			["DefaultRoleCode"] = "suplayer",
			["WorldConfig"] = new JsonObject { ["WorldConfiguration"] = new JsonObject { ["temporalStorms"] = "sometimes" }, ["Seed"] = "123" },
		};

		var applied = InitialConfig.Apply(generated, Spec(whitelist: true), Layout());

		Assert.Equal("creative", (string?) applied["ServerName"]);
		Assert.Equal("suplayer", (string?) applied["DefaultRoleCode"]);
		Assert.NotNull(applied["Roles"]);
		Assert.Equal(2, (int) applied["WhitelistMode"]!);
		Assert.Equal(42421, (int) applied["Port"]!);

		var world = applied["WorldConfig"]!.AsObject();
		Assert.Equal("sometimes", (string?) world["WorldConfiguration"]!["temporalStorms"]);
		Assert.Null(world["Seed"]);
		Assert.Equal("/srv/vs-panel/creative/data/Saves/default.vcdbs", (string?) world["SaveFileLocation"]);
		Assert.Equal("preset-creativebuilding", (string?) world["PlayStyleLangCode"]);

		// Исходный документ не изменился: значения накладываются на копию.
		Assert.Equal("123", (string?) generated["WorldConfig"]!["Seed"]);
	}

	[Fact]
	public void Первый_ключ_конфигурации_остаётся_на_своём_месте()
	{
		var generated = new JsonObject { ["ConfigVersion"] = "1.9", ["ServerName"] = "x", ["Port"] = 42420 };

		var applied = InitialConfig.Apply(generated, Spec(seed: "42"), Layout());

		Assert.Equal(["ConfigVersion", "ServerName", "Port"], applied.Select(pair => pair.Key).Take(3));
		Assert.Equal("42", (string?) applied["WorldConfig"]!["Seed"]);
	}
}
