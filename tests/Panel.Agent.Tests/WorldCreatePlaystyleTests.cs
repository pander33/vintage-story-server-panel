using System.IO.Compression;
using System.Text.Json.Nodes;
using Panel.Agent.Content;
using Panel.Agent.Runtime;
using Panel.Agent.Transport;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Tests;

/// <summary>
/// Создание мира: стиль проверяет демон по схеме сервера и берёт из неё ключ подписи. Без схемы —
/// встроенные стили игры с прежним правилом <c>"preset-" + код</c>.
/// </summary>
public sealed class WorldCreatePlaystyleTests : IDisposable
{
	private readonly string root = Path.Combine(Path.GetTempPath(), "world-create-style", Guid.NewGuid().ToString("n"));

	public WorldCreatePlaystyleTests() => Directory.CreateDirectory(root);

	public void Dispose()
	{
		try
		{
			Directory.Delete(root, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	[Fact]
	public async Task Стиль_мода_из_схемы_принимается_с_её_ключом_подписи()
	{
		if (!GameOwnership.Possible)
		{
			return;
		}

		var server = Server();
		WriteMod(server, """{"modid": "m", "version": "1.0", "worldConfig": {"playstyles": [{"code": "creativebuilding", "langCode": "creativebuilding"}, {"code": "mymod-hardcore", "langCode": "mymod-hardcore-lang"}]}}""");

		var creative = await CreateAsync(server, "creative.vcdbs", "creativebuilding");
		Assert.True(creative.Ok, creative.Error);
		Assert.Equal("creativebuilding", World(server)["PlayStyleLangCode"]!.GetValue<string>());

		var modded = await CreateAsync(server, "hard.vcdbs", "mymod-hardcore");
		Assert.True(modded.Ok, modded.Error);
		Assert.Equal("mymod-hardcore", World(server)["PlayStyle"]!.GetValue<string>());
		Assert.Equal("mymod-hardcore-lang", World(server)["PlayStyleLangCode"]!.GetValue<string>());
	}

	[Fact]
	public async Task Стиль_которого_нет_в_схеме_отклоняется()
	{
		var server = Server();
		WriteMod(server, """{"modid": "m", "version": "1.0", "worldConfig": {"playstyles": [{"code": "surviveandbuild", "langCode": "preset-surviveandbuild"}]}}""");

		var reply = await CreateAsync(server, "x.vcdbs", "exploration");

		Assert.False(reply.Ok);
		Assert.Equal("Недопустимый стиль игры", reply.Error);
	}

	[Fact]
	public async Task Без_схемы_принимаются_только_встроенные_стили_с_прежним_ключом()
	{
		if (!GameOwnership.Possible)
		{
			return;
		}

		var server = Server();

		var builtin = await CreateAsync(server, "a.vcdbs", "exploration");
		Assert.True(builtin.Ok, builtin.Error);
		Assert.Equal("preset-exploration", World(server)["PlayStyleLangCode"]!.GetValue<string>());

		var unknown = await CreateAsync(server, "b.vcdbs", "mymod-hardcore");
		Assert.False(unknown.Ok);
	}

	private async Task<AgentReply> CreateAsync(RegisteredServer server, string file, string style)
	{
		var staging = new StagingArea(Path.Combine(root, "vs-panel.json"));
		var operations = new ContentOperations(
			new ModBackend(staging, TimeProvider.System),
			new WorldBackend(staging, TimeProvider.System),
			new BackupBackend(TimeProvider.System),
			new StoppedService(),
			new HistoryBackend(),
			staging,
			new OperationTracker(TimeProvider.System),
			new WorldSchemaReader());

		return await operations.ExecuteAsync(
			new AgentRequest { Op = AgentProtocol.OpWorldCreate, Instance = "example", Args = [file, "Мир", "", style] },
			server,
			CancellationToken.None);
	}

	private static JsonObject World(RegisteredServer server) =>
		(JsonNode.Parse(File.ReadAllText(server.ConfigFile))!["WorldConfig"] as JsonObject)!;

	private RegisteredServer Server()
	{
		var data = Path.Combine(root, "data");
		var saves = Path.Combine(data, "Saves");
		var mods = Path.Combine(data, "Mods");
		var program = Path.Combine(root, "server");
		Directory.CreateDirectory(saves);
		Directory.CreateDirectory(mods);
		Directory.CreateDirectory(Path.Combine(program, "Mods"));
		File.WriteAllText(Path.Combine(data, "serverconfig.json"), new JsonObject
		{
			["WorldConfig"] = new JsonObject { ["SaveFileLocation"] = Path.Combine(saves, "default.vcdbs") },
		}.ToJsonString());
		return new RegisteredServer
		{
			Id = InstanceId.Parse("example"),
			Name = "Example",
			Unit = "vs-example",
			GameVersion = "1.22.7",
			ProgramDirectory = program,
			DataDirectory = data,
			WorldDirectory = saves,
			ModsDirectory = mods,
			ConfigFile = Path.Combine(data, "serverconfig.json"),
		};
	}

	private static void WriteMod(RegisteredServer server, string modinfo)
	{
		using var archive = ZipFile.Open(Path.Combine(server.ModsDirectory, "mod.zip"), ZipArchiveMode.Create);
		using var writer = new StreamWriter(archive.CreateEntry("modinfo.json").Open());
		writer.Write(modinfo);
	}

	private sealed class StoppedService : IServiceBackend
	{
		public string Name => "supervised";

		public Task<ServiceStatusPayload> StatusAsync(RegisteredServer server, CancellationToken ct) =>
			Task.FromResult(new ServiceStatusPayload { Active = false, Unit = server.Unit });

		public Task<bool> IsActiveAsync(RegisteredServer server, CancellationToken ct) => Task.FromResult(false);

		public Task ServiceAsync(RegisteredServer server, string action, TimeSpan timeout, CancellationToken ct) => Task.CompletedTask;

		public Task<IReadOnlyList<LogEntryPayload>> LogsAsync(RegisteredServer server, int count, CancellationToken ct) =>
			Task.FromResult<IReadOnlyList<LogEntryPayload>>([]);

		public Task<LogPagePayload> LogsFromAsync(RegisteredServer server, string cursor, CancellationToken ct) =>
			Task.FromResult(new LogPagePayload());

		public Task<ConsoleEchoPayload> ConsoleAsync(RegisteredServer server, string line, bool capture, CancellationToken ct) =>
			Task.FromResult(new ConsoleEchoPayload());

		public Task<ConsoleCapabilityPayload> CapabilitiesAsync(RegisteredServer server, CancellationToken ct) =>
			Task.FromResult(new ConsoleCapabilityPayload());
	}
}
