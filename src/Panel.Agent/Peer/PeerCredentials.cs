using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Panel.Agent.Peer;

/// <summary>
/// Процесс на другом конце Unix-сокета. Эти данные сообщает ядро, клиент подделать их не может.
/// </summary>
public readonly record struct PeerIdentity(int Pid, uint Uid, uint Gid);

/// <summary>
/// SO_PEERCRED через getsockopt. В .NET этот параметр сокета недоступен, поэтому P/Invoke.
/// </summary>
/// <remarks>
/// На этом держится ADR-02: долгоживущий root-демон на UDS допустим только с проверкой пира,
/// без неё root получает любой локальный процесс. Скрипт <c>scripts/spikes/peercred.sh</c>
/// проверяет на сервере, что возвращается uid вызывающего процесса, в том числе при socket
/// activation.
/// </remarks>
/// </remarks>
[SupportedOSPlatform("linux")]
public static class PeerCredentials
{
	private const int solSocket = 1;
	private const int soPeercred = 17;

	[StructLayout(LayoutKind.Sequential)]
	private struct Ucred
	{
		public int Pid;
		public uint Uid;
		public uint Gid;
	}

	[DllImport("libc", SetLastError = true)]
	private static extern int getsockopt(int sockfd, int level, int optname, ref Ucred optval, ref int optlen);

	public static PeerIdentity Of(Socket connected)
	{
		ArgumentNullException.ThrowIfNull(connected);

		var credentials = default(Ucred);
		var length = Marshal.SizeOf<Ucred>();
		if (getsockopt((int) connected.Handle, solSocket, soPeercred, ref credentials, ref length) != 0)
		{
			var errno = Marshal.GetLastPInvokeError();
			throw new IOException($"getsockopt(SO_PEERCRED) вернул ошибку {errno}");
		}

		return new PeerIdentity(credentials.Pid, credentials.Uid, credentials.Gid);
	}
}
