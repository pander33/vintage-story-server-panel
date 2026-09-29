using System.Net.Sockets;
using Panel.Agent.Content;
using Panel.Agent.Peer;
using Panel.SharedKernel;

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
	private readonly string? socketGroup;
	private readonly PeerPolicy peers;
	private readonly AgentConversation conversation;

	public AgentServer(string socketPath, string? socketGroup, PeerPolicy peers, AgentDispatcher dispatcher)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
		ArgumentNullException.ThrowIfNull(peers);
		ArgumentNullException.ThrowIfNull(dispatcher);

		this.socketPath = socketPath;
		this.socketGroup = socketGroup;
		this.peers = peers;
		conversation = new AgentConversation(dispatcher);
	}

	/// <summary>Слушать до отмены; отказ — только если сокет не удалось отдать группе панели.</summary>
	public async Task<Result<Done>> RunAsync(CancellationToken ct)
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

		if (socketGroup is not null)
		{
			// connect требует права записи на сокет, а создаёт его root: без группы панели
			// учётная запись vs-panel получала бы Permission denied. Каталог сокета не трогается:
			// --socket в общем каталоге отдал бы группе и его.
			var changed = await GameFiles.ChangeGroupAsync(socketPath, socketGroup, ct).ConfigureAwait(false);
			if (!changed.IsSuccess)
			{
				return changed;
			}
		}

		// Готовность — только после прав: systemctl restart в установщике ждёт её, и manage
		// сразу после него уже застаёт сокет открытым для панели.
		SystemdNotify.Ready();

		while (!ct.IsCancellationRequested)
		{
			var connection = await listener.AcceptAsync(ct).ConfigureAwait(false);
			_ = ServeAsync(connection, ct);
		}

		return Done.Ok;
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
