using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Panel.SharedKernel;

namespace Panel.Agent.Client;

/// <summary>
/// Кто открыл именованный канал демона на Windows. Панель доверяет каналу, только если его
/// сервер работает от LocalSystem или от той же учётной записи, что и она сама.
/// </summary>
/// <remarks>
/// <para>
/// Список доступа канала защищает демон от чужого клиента, но не панель от чужого сервера:
/// пока демон не запущен (перезапуск, обновление), имя <c>vs-panel-agent</c> может занять любой
/// процесс машины (findings §65). Панель подключилась бы к нему и поверила бы его ответам.
/// Сервер узнаётся по <c>GetNamedPipeServerProcessId</c> и учётной записи этого процесса.
/// </para>
/// <para>
/// Вторая половина защиты — уровень олицетворения <c>Identification</c> у клиента
/// (<see cref="AgentClient"/>): без него Windows разрешает серверу канала действовать от имени
/// клиента, а панель работает от LocalSystem.
/// </para>
/// <para>
/// Своя учётная запись допускается ради запуска из командной строки разработчика: процесс той же
/// учётной записи и так может всё, что может панель.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class PipeServerIdentity
{
	private const uint processQueryLimitedInformation = 0x1000;
	private const uint tokenQuery = 0x0008;

	public static Result<Done> Check(NamedPipeClientStream pipe)
	{
		ArgumentNullException.ThrowIfNull(pipe);

		if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
		{
			return Failure.Unavailable($"Не удалось узнать процесс, открывший канал привилегированной службы: {LastError()}");
		}

		var owner = OwnerOf(processId, out var error);
		if (owner is null)
		{
			return Failure.Unavailable($"Не удалось узнать учётную запись процесса {processId}, открывшего канал привилегированной службы: {error}");
		}

		using var self = WindowsIdentity.GetCurrent();
		return Trusted(owner, self.User)
			? Done.Ok
			: Failure.Unavailable($"Канал привилегированной службы открыт процессом {processId} от имени {owner.Value}, а не службой панели — соединение отвергнуто");
	}

	/// <summary>Сервер канала — LocalSystem или та же учётная запись, что у панели.</summary>
	public static bool Trusted(SecurityIdentifier server, SecurityIdentifier? self)
	{
		ArgumentNullException.ThrowIfNull(server);
		return server.IsWellKnown(WellKnownSidType.LocalSystemSid) || (self is not null && server == self);
	}

	/// <summary>
	/// Учётная запись процесса. Текст ошибки снимается сразу после отказавшего вызова: закрытие
	/// дескриптора процесса при выходе из <c>using</c> перезаписало бы код ошибки потока.
	/// </summary>
	private static SecurityIdentifier? OwnerOf(uint processId, out string error)
	{
		error = string.Empty;
		using var process = OpenProcess(processQueryLimitedInformation, false, processId);
		if (process.IsInvalid)
		{
			error = LastError();
			return null;
		}

		if (!OpenProcessToken(process, tokenQuery, out var token))
		{
			error = LastError();
			token.Dispose();
			return null;
		}

		using (token)
		{
			using var identity = new WindowsIdentity(token.DangerousGetHandle());
			return identity.User;
		}
	}

	private static string LastError() => new Win32Exception(Marshal.GetLastPInvokeError()).Message;

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern KernelHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

	[DllImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool OpenProcessToken(KernelHandle process, uint access, out KernelHandle token);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr handle);

	/// <summary>Дескриптор процесса или маркера доступа; закрывается <c>CloseHandle</c>.</summary>
	private sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
	{
		public KernelHandle()
			: base(ownsHandle: true)
		{
		}

		protected override bool ReleaseHandle() => CloseHandle(handle);
	}
}
