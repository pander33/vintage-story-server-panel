using System.Globalization;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Panel.Agent.Configuration;
using Panel.Agent.Content;
using Panel.Agent.Peer;
using Panel.Agent.Provisioning;
using Panel.Agent.Runtime;
using Panel.Agent.Supervision;
using Panel.Agent.Systemd;
using Panel.Agent.Telemetry;
using Panel.Agent.Transport;
using Panel.Fleet.Infrastructure;

namespace Panel.Agent;

/// <summary>
/// Привилегированная сторона панели. На Linux запускается от root, слушает Unix-сокет и
/// отвечает только процессам с разрешённым uid. На Windows работает службой от SYSTEM,
/// сам держит игровые серверы дочерними процессами и слушает именованный канал, список
/// доступа которого пускает только учётную запись панели.
/// </summary>
public static class Program
{
	public static async Task<int> Main(string[] args)
	{
		var options = AgentOptions.Parse(args);
		if (options is null)
		{
			Console.Error.WriteLine(
				"""
                Использование:
                  vs-panel-agent serve --socket <путь> --panel-config <путь> --allow-uid <uid> [--allow-uid <uid>…]
                  vs-panel-agent serve --pipe <имя> --panel-config <путь> [--allow-sid <SID|учётная запись>…]   Windows
                  vs-panel-agent probe-peer --socket <путь>          спайк SO_PEERCRED: сторона демона
                  vs-panel-agent probe-peer-client --socket <путь>   спайк SO_PEERCRED: сторона клиента
                """);
			return 2;
		}

		using var stopping = new CancellationTokenSource();
		Console.CancelKeyPress += (_, eventArgs) =>
		{
			eventArgs.Cancel = true;
			stopping.Cancel();
		};

		try
		{
			return options.Mode switch
			{
				AgentMode.Serve => await ServeAsync(options, stopping.Token).ConfigureAwait(false),
				AgentMode.ProbePeerServer => await ProbeServerAsync(options, stopping.Token).ConfigureAwait(false),
				AgentMode.ProbePeerClient => await ProbeClientAsync(options, stopping.Token).ConfigureAwait(false),
				_ => 2,
			};
		}
		catch (OperationCanceledException)
		{
			return 0;
		}
	}

	private static async Task<int> ServeAsync(AgentOptions options, CancellationToken ct)
	{
		if (OperatingSystem.IsWindows())
		{
			return await ServeWindowsAsync(options, ct).ConfigureAwait(false);
		}

		if (!OperatingSystem.IsLinux())
		{
			Console.Error.WriteLine("Демон работает на Linux и Windows; на этой платформе проверки пира нет.");
			return 3;
		}

		if (options.Socket is null || options.AllowedUids.Count == 0)
		{
			Console.Error.WriteLine("На Linux нужны --socket и хотя бы один --allow-uid.");
			return 2;
		}

		var configuration = await ReadConfigurationAsync(options.PanelConfig, ct).ConfigureAwait(false);
		if (configuration is null)
		{
			return 2;
		}

		var dispatcher = Compose(options.PanelConfig, new SystemdBackend(), new SystemdProvisioning(configuration.RunDirectory));
		var server = new AgentServer(options.Socket, new PeerPolicy(options.AllowedUids), dispatcher);

		Console.WriteLine(
			$"vs-panel-agent слушает {options.Socket}; разрешённые uid: {string.Join(", ", options.AllowedUids)}");
		await server.RunAsync(ct).ConfigureAwait(false);
		return 0;
	}

	/// <summary>
	/// Демон на Windows: супервизор серверов и именованный канал.
	/// </summary>
	/// <remarks>
	/// Группы операций те же, что на Linux; различаются бэкенд службы и платформа создания
	/// серверов. Права и владельцев файлов на Windows задаёт список доступа каталога
	/// (см. <see cref="GameFiles"/>).
	/// </remarks>
	[SupportedOSPlatform("windows")]
	private static async Task<int> ServeWindowsAsync(AgentOptions options, CancellationToken ct)
	{
		if (options.Pipe is null)
		{
			Console.Error.WriteLine("На Windows нужен --pipe.");
			return 2;
		}

		// Без --allow-sid канал открыт учётной записи, от которой запущен демон.
		List<SecurityIdentifier> allowed;
		try
		{
			allowed = options.AllowedAccounts.Count > 0
				? [.. options.AllowedAccounts.Select(PipeAgentServer.Resolve)]
				: [WindowsIdentity.GetCurrent().User!];
		}
		catch (Exception exception) when (exception is ArgumentException or IdentityNotMappedException or SystemException)
		{
			Console.Error.WriteLine($"Учётная запись из --allow-sid не найдена: {exception.Message}");
			return 2;
		}

		var configuration = await ReadConfigurationAsync(options.PanelConfig, ct).ConfigureAwait(false);
		if (configuration is null)
		{
			return 2;
		}

		var service = new SupervisedBackend(configuration.RunDirectory);
		var dispatcher = Compose(options.PanelConfig, service, new SupervisedProvisioning(service));

		var server = new PipeAgentServer(options.Pipe, allowed, dispatcher);
		Console.WriteLine(
			$"vs-panel-agent слушает {PipeAgentServer.PathPrefix}{PipeAgentServer.NameOf(options.Pipe)}; "
			+ $"разрешённые учётные записи: {string.Join(", ", allowed)}");

		// Под диспетчером служб демон должен отвечать ему, иначе через 30 секунд служба будет
		// снята. Из консоли он работает до Ctrl+C.
		if (!Environment.UserInteractive)
		{
			using var windowsService = new AgentWindowsService(server.RunAsync);
			System.ServiceProcess.ServiceBase.Run(windowsService);
			return windowsService.ExitCode;
		}

		await server.RunAsync(ct).ConfigureAwait(false);
		return 0;
	}

	/// <summary>
	/// Таблица операций демона, общая для обеих платформ.
	/// </summary>
	private static AgentDispatcher Compose(string panelConfig, IServiceBackend service, IProvisioningPlatform platform)
	{
		var registry = new FileRegistryStore(panelConfig);
		var staging = new StagingArea(panelConfig);
		var telemetry = new TelemetryFileReader();
		var backups = new BackupBackend(TimeProvider.System);
		var editor = new RegistryEditor(panelConfig);
		var tracker = new OperationTracker(TimeProvider.System);
		var schemas = new WorldSchemaReader();
		var content = new ContentOperations(
			new ModBackend(staging, TimeProvider.System),
			new WorldBackend(staging, TimeProvider.System),
			backups,
			service,
			new HistoryBackend(),
			staging,
			tracker,
			schemas);

		return new AgentDispatcher(
			registry,
			service,
			telemetry,
			new GameEventLog(),
			[
				content,
				new ConfigOperations(new ConfigBackend(staging, TimeProvider.System)),
				new WorldSchemaOperations(schemas),
				new MetricsCollector(service, telemetry, backups),
				new GameOperations(new GameBackend(staging, TimeProvider.System), service, editor),
				new PlayerListOperations(new PlayerListsBackend()),
			],
			[
				new RegistryOperations(registry, editor, service),
				new ProvisionOperations(new ProvisionBackend(panelConfig, registry, editor, staging, platform)),
			],
			tracker);
	}

	/// <summary>Настройки панели или <see langword="null"/> с причиной в stderr.</summary>
	private static async Task<PanelConfiguration?> ReadConfigurationAsync(string path, CancellationToken ct)
	{
		string json;
		try
		{
			json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Console.Error.WriteLine($"Не читается {path}: {exception.Message}");
			return null;
		}

		var configuration = PanelConfiguration.Parse(json);
		if (!configuration.IsSuccess)
		{
			Console.Error.WriteLine(configuration.Failure!.Message);
			return null;
		}

		return configuration.Value;
	}

	/// <summary>
	/// Сторона демона для <c>scripts/spikes/peercred.sh</c>: печатает результат SO_PEERCRED и
	/// отвечает тем же текстом.
	/// </summary>
	private static async Task<int> ProbeServerAsync(AgentOptions options, CancellationToken ct)
	{
		if (!OperatingSystem.IsLinux())
		{
			Console.Error.WriteLine("Спайк SO_PEERCRED выполняется только на Linux.");
			return 3;
		}

		if (File.Exists(options.Socket!))
		{
			File.Delete(options.Socket!);
		}

		using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
		listener.Bind(new UnixDomainSocketEndPoint(options.Socket!));
		listener.Listen(4);
		Console.WriteLine($"probe-peer: слушаю {options.Socket}");

		while (!ct.IsCancellationRequested)
		{
			using var connection = await listener.AcceptAsync(ct).ConfigureAwait(false);
			string answer;
			try
			{
				var peer = PeerCredentials.Of(connection);
				answer = $"pid={peer.Pid} uid={peer.Uid} gid={peer.Gid}";
			}
			catch (IOException exception)
			{
				answer = $"ОШИБКА {exception.Message}";
			}

			Console.WriteLine($"probe-peer: подключился {answer}");
			await connection.SendAsync(Encoding.UTF8.GetBytes(answer + "\n"), SocketFlags.None, ct)
				.ConfigureAwait(false);
		}

		return 0;
	}

	private static async Task<int> ProbeClientAsync(AgentOptions options, CancellationToken ct)
	{
		using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
		await client.ConnectAsync(new UnixDomainSocketEndPoint(options.Socket!), ct).ConfigureAwait(false);

		var buffer = new byte[256];
		var read = await client.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
		Console.WriteLine($"probe-peer-client: демон увидел {Encoding.UTF8.GetString(buffer, 0, read).Trim()}");
		return 0;
	}
}

internal enum AgentMode
{
	Serve,
	ProbePeerServer,
	ProbePeerClient,
}

internal sealed record AgentOptions
{
	public required AgentMode Mode { get; init; }

	public string? Socket { get; init; }

	/// <summary>Имя именованного канала Windows, с префиксом <c>\\.\pipe\</c> или без.</summary>
	public string? Pipe { get; init; }

	public string PanelConfig { get; init; } = DefaultPanelConfig;

	public IReadOnlyList<uint> AllowedUids { get; init; } = [];

	/// <summary>SID или имена учётных записей, которым открыт канал Windows.</summary>
	public IReadOnlyList<string> AllowedAccounts { get; init; } = [];

	/// <summary>Путь по умолчанию для каждой платформы.</summary>
	private static string DefaultPanelConfig => OperatingSystem.IsWindows()
		? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "vs-panel", "vs-panel.json")
		: "/etc/vs-panel.json";

	public static AgentOptions? Parse(string[] args)
	{
		if (args.Length == 0)
		{
			return null;
		}

		var mode = args[0] switch
		{
			"serve" => AgentMode.Serve,
			"probe-peer" => AgentMode.ProbePeerServer,
			"probe-peer-client" => AgentMode.ProbePeerClient,
			_ => (AgentMode?) null,
		};

		if (mode is null)
		{
			return null;
		}

		string? socket = null;
		string? pipe = null;
		var panelConfig = DefaultPanelConfig;
		var uids = new List<uint>();
		var accounts = new List<string>();

		for (var index = 1; index < args.Length - 1; index += 2)
		{
			switch (args[index])
			{
				case "--socket":
					socket = args[index + 1];
					break;
				case "--pipe":
					pipe = args[index + 1];
					break;
				case "--allow-sid":
					accounts.Add(args[index + 1]);
					break;
				case "--panel-config":
					panelConfig = args[index + 1];
					break;
				case "--allow-uid":
					if (!uint.TryParse(args[index + 1], CultureInfo.InvariantCulture, out var uid))
					{
						return null;
					}

					uids.Add(uid);
					break;
				default:
					return null;
			}
		}

		// Проверкам нужен сокет; serve сам решает, что использовать: сокет или канал.
		if (mode is AgentMode.Serve ? socket is null && pipe is null : socket is null)
		{
			return null;
		}

		return new AgentOptions
		{
			Mode = mode.Value,
			Socket = socket,
			Pipe = pipe,
			PanelConfig = panelConfig,
			AllowedUids = uids,
			AllowedAccounts = accounts,
		};
	}
}
