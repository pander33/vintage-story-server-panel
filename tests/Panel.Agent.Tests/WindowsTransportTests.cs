using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Panel.Agent.Client;
using Panel.Agent.Supervision;
using Panel.Agent.Telemetry;
using Panel.Agent.Transport;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.SharedKernel;

namespace Panel.Agent.Tests;

/// <summary>
/// Платформенная часть Windows, которую можно проверить только на Windows: список доступа
/// канала, разговор по каналу и запуск дочернего процесса с наследованием дескрипторов. На
/// других платформах тесты ничего не делают.
/// </summary>
/// <remarks>
/// Дочерний процесс здесь — <c>cmd.exe</c>: проверяется только, что stdin доходит до процесса,
/// а его вывод дописывается в журнал.
/// </remarks>
public sealed class WindowsTransportTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "vs-win-" + Guid.NewGuid().ToString("n"));

	public void Dispose()
	{
		if (Directory.Exists(directory))
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	[SupportedOSPlatform("windows")]
	public void Канал_закрыт_для_сети_и_не_даёт_создавать_экземпляры()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		var panel = new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null);
		var rules = PipeAgentServer.Security([panel])
			.GetAccessRules(true, false, typeof(SecurityIdentifier))
			.Cast<PipeAccessRule>()
			.ToList();

		var network = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
		Assert.Contains(rules, rule => rule.IdentityReference == network && rule.AccessControlType == AccessControlType.Deny);

		var granted = Assert.Single(rules, rule => rule.IdentityReference == panel);
		Assert.Equal(AccessControlType.Allow, granted.AccessControlType);
		Assert.True(granted.PipeAccessRights.HasFlag(PipeAccessRights.ReadWrite));
		Assert.False(granted.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance));
	}

	[Fact]
	[SupportedOSPlatform("windows")]
	public void Имя_канала_принимается_с_префиксом_и_без()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		Assert.Equal("vs-panel-agent", PipeAgentServer.NameOf(@"\\.\pipe\vs-panel-agent"));
		Assert.Equal("vs-panel-agent", PipeAgentServer.NameOf("vs-panel-agent"));
	}

	[Fact]
	[SupportedOSPlatform("windows")]
	public async Task Панель_говорит_с_демоном_по_каналу()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		var name = "vs-panel-test-" + Guid.NewGuid().ToString("n");
		using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		var server = new PipeAgentServer(name, [WindowsIdentity.GetCurrent().User!], Dispatcher());
		var serving = server.RunAsync(stopping.Token);

		var client = new AgentClient(AgentClient.PipePrefix + name);
		var replies = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
			client.CallAsync<AcknowledgedPayload>(AgentProtocol.OpPing, InstanceId.Parse("main"), [], stopping.Token)));

		await stopping.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serving);
		Assert.All(replies, reply => Assert.True(reply.IsSuccess && reply.Value.Ok));
	}

	/// <summary>
	/// Если имя канала занято, демон не стартует, а не подключается к чужому каналу вторым
	/// экземпляром.
	/// </summary>
	[Fact]
	[SupportedOSPlatform("windows")]
	public async Task Занятое_имя_канала_не_принимается()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		var name = "vs-panel-test-" + Guid.NewGuid().ToString("n");
		await using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances);
		var server = new PipeAgentServer(name, [WindowsIdentity.GetCurrent().User!], Dispatcher());

		await Assert.ThrowsAnyAsync<Exception>(() => server.RunAsync(CancellationToken.None));
	}

	[Fact]
	[SupportedOSPlatform("windows")]
	public async Task Дочерний_процесс_читает_stdin_и_дописывает_журнал()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		Directory.CreateDirectory(directory);
		var log = Path.Combine(directory, "panel-console.log");
		File.WriteAllText(log, "прежний вывод\n");

		var child = ChildProcess.Start(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/q"], directory, log);
		await using (child.Input)
		{
			await child.Input.WriteAsync(Encoding.ASCII.GetBytes("echo vs-marker\r\nexit\r\n"));
			await child.Input.FlushAsync();
		}

		using (var process = Process.GetProcessById(child.Pid))
		{
			Assert.True(process.WaitForExit(TimeSpan.FromSeconds(15)));
		}

		var text = File.ReadAllText(log);
		Assert.StartsWith("прежний вывод", text);
		Assert.Contains("vs-marker", text);
	}

	[SupportedOSPlatform("windows")]
	private static AgentDispatcher Dispatcher() =>
		new(new EmptyRegistry(), new NoBackend(), new TelemetryFileReader(), new GameEventLog(), [], []);

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
