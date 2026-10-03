using System.Text;
using System.Text.Json;
using Panel.Agent.Telemetry;
using Panel.Agent.Transport;
using Panel.Contracts;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;

namespace Panel.Agent.Tests;

/// <summary>
/// Разговор демона с панелью без транспорта: строка запроса на входе, строка ответа на выходе.
/// </summary>
public sealed class AgentConversationTests
{
	[Fact]
	public async Task Запрос_своей_сборки_выполняется()
	{
		var reply = await TalkAsync(new { v = 1, build = AgentProtocol.Build, op = AgentProtocol.OpPing, instance = "main" });

		Assert.True(reply.Ok);
		Assert.Equal(AgentProtocol.Build, reply.Build);
	}

	/// <summary>
	/// Панель прежней сборки поля не присылает, другая присылает своё. Протокол тот же, поэтому
	/// запрос выполняется: отказ превратил бы незавершённое обновление в простой (findings §82).
	/// </summary>
	[Theory]
	[InlineData(null)]
	[InlineData("0.0.1+other")]
	public async Task Запрос_чужой_сборки_того_же_протокола_выполняется(string? build)
	{
		var reply = await TalkAsync(new { v = 1, build, op = AgentProtocol.OpPing, instance = "main" });

		Assert.True(reply.Ok);
		Assert.Equal(AgentProtocol.Build, reply.Build);
	}

	[Fact]
	public async Task Запрос_другого_протокола_отвергается()
	{
		var reply = await TalkAsync(new { v = AgentProtocol.Version + 1, build = AgentProtocol.Build, op = AgentProtocol.OpPing, instance = "main" });

		Assert.False(reply.Ok);
		Assert.Equal("version", reply.Code);
	}

	private static async Task<AgentReply> TalkAsync(object request)
	{
		var input = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n"));
		var output = new MemoryStream();
		var conversation = new AgentConversation(
			new AgentDispatcher(new EmptyRegistry(), new NoBackend(), new TelemetryFileReader(), new GameEventLog(), [], []));

		await conversation.RunAsync(new Duplex(input, output), CancellationToken.None);

		var line = Encoding.UTF8.GetString(output.ToArray()).TrimEnd('\n');
		return JsonSerializer.Deserialize<AgentReply>(line, PanelJson.Options)!;
	}

	/// <summary>Чтение из одного потока, запись в другой.</summary>
	private sealed class Duplex(Stream input, Stream output) : Stream
	{
		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => true;

		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override void Flush() => output.Flush();

		public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
	}

	private sealed class EmptyRegistry : IRegistryStore
	{
		public Task<RegistrySnapshot> LoadAsync(CancellationToken ct) =>
			Task.FromResult<RegistrySnapshot>(new RegistrySnapshot.Available([], null, RegistryStamp.Absent));
	}

	private sealed class NoBackend : Runtime.IServiceBackend
	{
		public string Name => "test";

		public Task<ServiceStatusPayload> StatusAsync(RegisteredServer server, CancellationToken ct) => throw new NotSupportedException();

		public Task<bool> IsActiveAsync(RegisteredServer server, CancellationToken ct) => throw new NotSupportedException();

		public Task ServiceAsync(RegisteredServer server, string action, TimeSpan timeout, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<IReadOnlyList<LogEntryPayload>> LogsAsync(RegisteredServer server, int count, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<LogPagePayload> LogsFromAsync(RegisteredServer server, string cursor, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<ConsoleEchoPayload> ConsoleAsync(RegisteredServer server, string line, bool capture, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<ConsoleCapabilityPayload> CapabilitiesAsync(RegisteredServer server, CancellationToken ct) =>
			throw new NotSupportedException();
	}
}
