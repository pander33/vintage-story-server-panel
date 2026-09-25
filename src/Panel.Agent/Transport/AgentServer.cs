using System.Net.Sockets;
using Panel.Agent.Peer;

namespace Panel.Agent.Transport;

/// <summary>
/// Демон привилегированной стороны: слушает Unix-сокет, проверяет пира, отвечает строкой
/// JSON на строку JSON (ADR-02, вариант B).
/// </summary>
/// <remarks>
/// Изменяемых полей уровня класса нет: всё, что относится к запросу, живёт в локальных
/// переменных.
///
/// Сам разговор ведёт <see cref="AgentConversation"/>, общий с каналом Windows
/// (<see cref="PipeAgentServer"/>); здесь только сокет и проверка пира.
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class AgentServer
{
	private readonly string socketPath;
	private readonly PeerPolicy peers;
	private readonly AgentConversation conversation;

	public AgentServer(string socketPath, PeerPolicy peers, AgentDispatcher dispatcher)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
		ArgumentNullException.ThrowIfNull(peers);
		ArgumentNullException.ThrowIfNull(dispatcher);

		this.socketPath = socketPath;
		this.peers = peers;
		conversation = new AgentConversation(dispatcher);
	}

	public async Task RunAsync(CancellationToken ct)
	{
		if (File.Exists(socketPath))
		{
			// Сокет от прошлого запуска: bind на существующий путь не пройдёт.
			File.Delete(socketPath);
		}

		using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
		listener.Bind(new UnixDomainSocketEndPoint(socketPath));
		listener.Listen(16);

		if (!OperatingSystem.IsWindows())
		{
			// Права на сокет дополняют SO_PEERCRED, но не заменяют его.
			File.SetUnixFileMode(
				socketPath,
				UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
		}

		while (!ct.IsCancellationRequested)
		{
			var connection = await listener.AcceptAsync(ct).ConfigureAwait(false);
			_ = ServeAsync(connection, ct);
		}
	}

	private async Task ServeAsync(Socket connection, CancellationToken ct)
	{
		using (connection)
		{
			await using var stream = new NetworkStream(connection, ownsSocket: false);

			var decision = peers.Decide(connection);
			if (decision is PeerDecision.Rejected rejected)
			{
				await AgentConversation.RefuseAsync(stream, "peer", rejected.Reason, ct).ConfigureAwait(false);
				return;
			}

			await conversation.RunAsync(stream, ct).ConfigureAwait(false);
		}
	}
}
