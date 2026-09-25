using System.Text.Json.Nodes;
using Panel.Agent.Runtime;
using Panel.Agent.Telemetry;
using Panel.Agent.Transport;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.SharedKernel;

namespace Panel.Agent.Tests;

/// <summary>
/// Диспетчер без платформенных групп, как на Windows. Операция, которой нет в этой сборке,
/// получает «не поддерживается», а не «неизвестная операция».
/// </summary>
public sealed class AgentDispatcherTests
{
	private static readonly RegisteredServer main = new()
	{
		Id = InstanceId.Parse("main"),
		Name = "Main",
		Unit = "vs-main",
		CommandTransport = "supervised",
	};

	private readonly FakeBackend backend = new();

	private AgentDispatcher Dispatcher(
		IReadOnlyList<IServerOperations>? server = null, IReadOnlyList<IRegistryOperations>? registry = null) =>
		new(new FakeRegistry(), backend, new TelemetryFileReader(), new GameEventLog(), server ?? [], registry ?? []);

	private static AgentRequest Request(string op, string instance = "main", params string[] args) =>
		new() { Op = op, Instance = instance, Args = args };

	[Fact]
	public async Task Операция_без_группы_не_поддерживается()
	{
		var reply = await Dispatcher().DispatchAsync(Request(AgentProtocol.OpMods), CancellationToken.None);

		Assert.False(reply.Ok);
		Assert.Equal("unsupported", reply.Code);
		Assert.Contains("supervised", reply.Error);
	}

	[Fact]
	public async Task Операция_реестра_без_группы_не_поддерживается()
	{
		var reply = await Dispatcher().DispatchAsync(Request(AgentProtocol.OpInstanceCreate, "-"), CancellationToken.None);

		Assert.Equal("unsupported", reply.Code);
	}

	[Fact]
	public async Task Незнакомое_имя_остаётся_неизвестной_операцией()
	{
		var reply = await Dispatcher().DispatchAsync(Request("rm-rf"), CancellationToken.None);

		Assert.Equal("op", reply.Code);
	}

	/// <summary>Операции службы есть на любой платформе и не адресуются реестру.</summary>
	[Fact]
	public async Task Статус_реестру_не_адресуется()
	{
		var reply = await Dispatcher().DispatchAsync(Request(AgentProtocol.OpStatus, "-"), CancellationToken.None);

		Assert.Equal("instance", reply.Code);
	}

	[Fact]
	public async Task Статус_идёт_в_бэкенд()
	{
		var reply = await Dispatcher().DispatchAsync(Request(AgentProtocol.OpStatus), CancellationToken.None);

		Assert.True(reply.Ok);
		Assert.Equal("vs-main", (string?) reply.Result!["unit"]);
	}

	[Fact]
	public async Task Группа_получает_свою_операцию()
	{
		var group = new FakeGroup(AgentProtocol.OpMods);

		var reply = await Dispatcher(server: [group]).DispatchAsync(Request(AgentProtocol.OpMods), CancellationToken.None);

		Assert.True(reply.Ok);
		Assert.Equal(1, group.Calls);
	}

	[Fact]
	public async Task Операция_реестра_серверу_не_адресуется()
	{
		var registry = new FakeRegistryGroup(AgentProtocol.OpInstances);

		var reply = await Dispatcher(registry: [registry]).DispatchAsync(Request(AgentProtocol.OpInstances), CancellationToken.None);

		Assert.Equal("instance", reply.Code);
		Assert.Equal(0, registry.Calls);
	}

	/// <summary>Консоль узнаёт у бэкенда, запущен ли сервер.</summary>
	[Fact]
	public async Task Команда_остановленному_серверу_отказ()
	{
		backend.Active = false;

		var reply = await Dispatcher().DispatchAsync(Request(AgentProtocol.OpConsole, "main", "stats"), CancellationToken.None);

		Assert.Equal("conflict", reply.Code);
		Assert.Empty(backend.Written);
	}

	private sealed class FakeRegistry : IRegistryStore
	{
		public Task<RegistrySnapshot> LoadAsync(CancellationToken ct) =>
			Task.FromResult<RegistrySnapshot>(new RegistrySnapshot.Available([main], main.Id, RegistryStamp.Absent));
	}

	private sealed class FakeGroup(string op) : IServerOperations
	{
		public int Calls { get; private set; }

		public bool Serves(string candidate) => candidate == op;

		public Task<AgentReply> ExecuteAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
		{
			Calls++;
			return Task.FromResult(AgentReply.Success(new JsonObject { ["ok"] = true }));
		}
	}

	private sealed class FakeRegistryGroup(string op) : IRegistryOperations
	{
		public int Calls { get; private set; }

		public bool Serves(string candidate) => candidate == op;

		public Task<AgentReply> ExecuteAsync(AgentRequest request, CancellationToken ct)
		{
			Calls++;
			return Task.FromResult(AgentReply.Success(new JsonObject { ["ok"] = true }));
		}
	}

	private sealed class FakeBackend : IServiceBackend
	{
		public bool Active { get; set; } = true;

		public List<string> Written { get; } = [];

		public string Name => "supervised";

		public Task<ServiceStatusPayload> StatusAsync(RegisteredServer server, CancellationToken ct) =>
			Task.FromResult(new ServiceStatusPayload { Active = Active, Unit = server.Unit });

		public Task<bool> IsActiveAsync(RegisteredServer server, CancellationToken ct) => Task.FromResult(Active);

		public Task ServiceAsync(RegisteredServer server, string action, TimeSpan timeout, CancellationToken ct) =>
			Task.CompletedTask;

		public Task<IReadOnlyList<LogEntryPayload>> LogsAsync(RegisteredServer server, int count, CancellationToken ct) =>
			Task.FromResult<IReadOnlyList<LogEntryPayload>>([]);

		public Task<LogPagePayload> LogsFromAsync(RegisteredServer server, string cursor, CancellationToken ct) =>
			Task.FromResult(new LogPagePayload());

		public Task<ConsoleEchoPayload> ConsoleAsync(RegisteredServer server, string line, bool capture, CancellationToken ct)
		{
			Written.Add(line);
			return Task.FromResult(new ConsoleEchoPayload());
		}

		public Task<ConsoleCapabilityPayload> CapabilitiesAsync(RegisteredServer server, CancellationToken ct) =>
			Task.FromResult(new ConsoleCapabilityPayload());
	}
}
