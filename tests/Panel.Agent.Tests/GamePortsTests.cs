using System.Text.Json.Nodes;
using Panel.Agent.Provisioning;
using Panel.Fleet.Domain;
using Panel.Fleet.Infrastructure;
using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Tests;

/// <summary>
/// Игровой порт следует за настройками: реестр и брандмауэр узнают о смене порта, чужой порт
/// не занимается и не закрывается.
/// </summary>
public sealed class GamePortsTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "vs-ports-" + Guid.NewGuid().ToString("n"));
	private readonly string panelConfig;
	private readonly string instancesFile;
	private readonly FakeFirewall firewall = new();
	private readonly GamePorts ports;

	public GamePortsTests()
	{
		Directory.CreateDirectory(directory);
		panelConfig = Path.Combine(directory, "vs-panel.json");
		instancesFile = Path.Combine(directory, "instances.json");
		File.WriteAllText(panelConfig, new JsonObject { ["version"] = 2, ["instances_file"] = instancesFile }.ToJsonString());
		File.WriteAllText(instancesFile, """
			{"default": "main", "instances": {
			  "main": {"name": "Main", "port": 42420, "data_dir": "/srv/main/data"},
			  "second": {"name": "Second", "port": 42421, "data_dir": "/srv/second/data"}
			}}
			""");
		ports = new GamePorts(new FileRegistryStore(panelConfig), new RegistryEditor(panelConfig), firewall);
	}

	public void Dispose()
	{
		if (Directory.Exists(directory))
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public async Task Смена_порта_переносит_правило_и_запись_реестра()
	{
		var warning = await ports.MoveAsync(Id("main"), 42420, 42500, CancellationToken.None);

		Assert.Null(warning);
		Assert.Equal([42420], firewall.Closed);
		Assert.Equal([42500], firewall.Opened);
		Assert.Equal(42500, Registry()["instances"]!["main"]!["port"]!.GetValue<int>());
	}

	[Fact]
	public async Task Прежний_порт_не_меняется_без_правил()
	{
		Assert.Null(await ports.MoveAsync(Id("main"), 42420, 42420, CancellationToken.None));
		Assert.Empty(firewall.Opened);
		Assert.Empty(firewall.Closed);
	}

	[Fact]
	public async Task Отказ_брандмауэра_возвращается_оператору()
	{
		firewall.Refusal = Phrase.From("ufw отказал");

		var warning = await ports.MoveAsync(Id("main"), 42420, 42500, CancellationToken.None);

		Assert.Equal(firewall.Refusal, warning);
		Assert.Equal(42500, Registry()["instances"]!["main"]!["port"]!.GetValue<int>());
	}

	[Fact]
	public async Task Порт_другого_сервера_занят()
	{
		var taken = await ports.TakenAsync(Id("main"), CancellationToken.None);

		var clash = GamePorts.Clash(new JsonObject { ["Port"] = 42421 }, taken);

		Assert.NotNull(clash);
		Assert.Equal(FailureCode.Conflict, clash.Code);
		Assert.Null(GamePorts.Clash(new JsonObject { ["Port"] = 42420 }, taken));
		Assert.Null(GamePorts.Clash(new JsonObject { ["Port"] = 42600 }, taken));
	}

	[Fact]
	public async Task Порт_другого_сервера_не_закрывается()
	{
		await ports.CloseAsync(Id("main"), 42421, CancellationToken.None);
		await ports.CloseAsync(Id("main"), 42420, CancellationToken.None);

		Assert.Equal([42420], firewall.Closed);
	}

	[Fact]
	public void Правила_ufw_и_netsh_для_TCP_и_UDP()
	{
		Assert.Equal([["allow", "42420/tcp"], ["allow", "42420/udp"]], GamePortRule.UfwAllow(42420));
		Assert.Equal([["delete", "allow", "42420/tcp"], ["delete", "allow", "42420/udp"]], GamePortRule.UfwDelete(42420));

		// Имена совпадают с правилами установщиков прежних версий: их тоже можно снять.
		Assert.Equal("name=Vintage Story 42420 TCP", GamePortRule.NetshAdd(42420)[0][4]);
		Assert.Equal("name=Vintage Story 42420 UDP", GamePortRule.NetshDelete(42420)[1][4]);
		Assert.Contains("protocol=UDP", GamePortRule.NetshAdd(42420)[1]);
		Assert.False(GamePortRule.IsValid(22));
		Assert.True(GamePortRule.IsValid(42420));
	}

	private JsonNode Registry() => JsonNode.Parse(File.ReadAllText(instancesFile))!;

	private static InstanceId Id(string value) => InstanceId.Parse(value);

	private sealed class FakeFirewall : IGameFirewall
	{
		public List<int> Opened { get; } = [];

		public List<int> Closed { get; } = [];

		public Phrase? Refusal { get; set; }

		public Task<Phrase?> OpenAsync(int port, CancellationToken ct)
		{
			Opened.Add(port);
			return Task.FromResult(Refusal);
		}

		public Task CloseAsync(int port, CancellationToken ct)
		{
			Closed.Add(port);
			return Task.CompletedTask;
		}
	}
}
