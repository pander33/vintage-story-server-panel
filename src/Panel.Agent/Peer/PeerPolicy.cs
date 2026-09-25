using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Panel.Agent.Peer;

/// <summary>Решение о допуске подключившегося процесса.</summary>
public abstract record PeerDecision
{
	private PeerDecision()
	{
	}

	public sealed record Accepted(PeerIdentity Peer) : PeerDecision;

	public sealed record Rejected(string Reason) : PeerDecision;
}

/// <summary>
/// Кому разрешено подключаться к привилегированной стороне: список uid. Сокет в /run
/// доступен любому, кто до него доберётся.
/// </summary>
public sealed class PeerPolicy
{
	private readonly HashSet<uint> allowed;

	public PeerPolicy(IEnumerable<uint> allowedUids)
	{
		ArgumentNullException.ThrowIfNull(allowedUids);
		allowed = [.. allowedUids];
		if (allowed.Count == 0)
		{
			throw new ArgumentException("Список разрешённых uid пуст: это открыло бы демон всем", nameof(allowedUids));
		}
	}

	public PeerDecision Decide(Socket connected)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			// На Windows доступ к каналу ограничивает ACL, а не SO_PEERCRED. Без проверки пира
			// подключение отклоняется.
			return new PeerDecision.Rejected("Проверка пира реализована только для Linux");
		}

		PeerIdentity peer;
		try
		{
			peer = PeerCredentials.Of(connected);
		}
		catch (IOException exception)
		{
			return new PeerDecision.Rejected(exception.Message);
		}

		return allowed.Contains(peer.Uid)
			? new PeerDecision.Accepted(peer)
			: new PeerDecision.Rejected($"uid {peer.Uid} не разрешён");
	}
}
