using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Panel.Agent.Supervision;

/// <summary>Запущенный сервер: pid и конец канала, ведущий в его stdin.</summary>
public sealed record StartedChild(int Pid, AnonymousPipeServerStream Input);

/// <summary>
/// Запуск игрового сервера дочерним процессом демона на Windows.
/// </summary>
/// <remarks>
/// <see cref="System.Diagnostics.Process"/> перенаправляет вывод только в канал, который
/// читает родитель. Такой канал рвётся вместе с демоном, и после перезапуска службы вывод
/// сервера терялся бы. Поэтому stdout и stderr здесь — дескриптор файла журнала, открытый
/// на дописывание (<c>FILE_APPEND_DATA</c>): сервер пишет в него сам, в том числе пока
/// демона нет.
///
/// Наследуются только два дескриптора, stdin и журнал, через
/// <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>. Иначе сервер, запущенный одновременно с другим,
/// унаследовал бы чужой канал stdin, и тот никогда не получил бы конца файла.
///
/// Если разрешено, сервер выводится из задания (job object) демона: службу, запущенную
/// планировщиком, Windows останавливает вместе со всем заданием, а сервер должен пережить
/// перезапуск демона. Если выход запрещён, сервер запускается внутри задания.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ChildProcess
{
	private const uint fileAppendData = 0x0004;
	private const uint synchronize = 0x0010_0000;
	private const uint fileShareAll = 0x1 | 0x2 | 0x4;
	private const uint openAlways = 4;
	private const uint fileAttributeNormal = 0x80;

	private const int startfUseStdHandles = 0x100;
	private const uint extendedStartupInfoPresent = 0x0008_0000;
	private const uint createNewProcessGroup = 0x0000_0200;
	private const uint createNoWindow = 0x0800_0000;
	private const uint createBreakawayFromJob = 0x0100_0000;
	private const int errorAccessDenied = 5;

	private static readonly IntPtr procThreadAttributeHandleList = 0x0002_0002;

	/// <summary>
	/// Запустить <paramref name="executable"/> с аргументами: stdin из канала, stdout и stderr
	/// дописываются в <paramref name="logPath"/>.
	/// </summary>
	public static StartedChild Start(
		string executable, IReadOnlyList<string> arguments, string workingDirectory, string logPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executable);
		ArgumentNullException.ThrowIfNull(arguments);
		ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(logPath);

		var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
		try
		{
			var inheritable = new SecurityAttributes
			{
				Length = Marshal.SizeOf<SecurityAttributes>(),
				InheritHandle = 1,
			};

			using var log = CreateFile(
				logPath, fileAppendData | synchronize, fileShareAll, ref inheritable, openAlways, fileAttributeNormal, IntPtr.Zero);
			if (log.IsInvalid)
			{
				throw Failure($"Не удалось открыть журнал консоли {logPath}", Marshal.GetLastPInvokeError());
			}

			var pid = Launch(
				executable,
				arguments,
				workingDirectory,
				input.ClientSafePipeHandle.DangerousGetHandle(),
				log.DangerousGetHandle());

			// Дескриптор для чтения теперь есть у сервера. Свою копию нужно закрыть, иначе канал
			// не закроется, когда сервер завершится.
			input.DisposeLocalCopyOfClientHandle();
			return new StartedChild(pid, input);
		}
		catch
		{
			input.Dispose();
			throw;
		}
	}

	private static int Launch(
		string executable, IReadOnlyList<string> arguments, string workingDirectory, IntPtr input, IntPtr output)
	{
		var size = IntPtr.Zero;

		// Первый вызов только сообщает нужный размер и возвращает ошибку «мало места».
		_ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);

		var attributes = Marshal.AllocHGlobal(size);
		var handles = Marshal.AllocHGlobal(IntPtr.Size * 2);
		var initialized = false;
		try
		{
			if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
			{
				throw Failure("Не удалось подготовить список наследуемых дескрипторов", Marshal.GetLastPInvokeError());
			}

			initialized = true;
			Marshal.WriteIntPtr(handles, 0, input);
			Marshal.WriteIntPtr(handles, IntPtr.Size, output);

			if (!UpdateProcThreadAttribute(
				attributes, 0, procThreadAttributeHandleList, handles, IntPtr.Size * 2, IntPtr.Zero, IntPtr.Zero))
			{
				throw Failure("Не удалось ограничить наследуемые дескрипторы", Marshal.GetLastPInvokeError());
			}

			var startup = new StartupInfoEx
			{
				StartupInfo = new StartupInfo
				{
					Cb = Marshal.SizeOf<StartupInfoEx>(),
					Flags = startfUseStdHandles,
					StdInput = input,
					StdOutput = output,
					StdError = output,
				},
				AttributeList = attributes,
			};

			var commandLine = WindowsCommandLine.Build([executable, .. arguments]);
			const uint flags = extendedStartupInfoPresent | createNewProcessGroup | createNoWindow;

			if (!CreateProcess(executable, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true,
				flags | createBreakawayFromJob, IntPtr.Zero, workingDirectory, ref startup, out var info))
			{
				var error = Marshal.GetLastPInvokeError();
				if (error != errorAccessDenied)
				{
					throw Failure($"Не удалось запустить {executable}", error);
				}

				// Задание демона не разрешает выход из себя.
				if (!CreateProcess(executable, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true,
					flags, IntPtr.Zero, workingDirectory, ref startup, out info))
				{
					throw Failure($"Не удалось запустить {executable}", Marshal.GetLastPInvokeError());
				}
			}

			CloseHandle(info.Thread);
			CloseHandle(info.Process);
			return info.ProcessId;
		}
		finally
		{
			if (initialized)
			{
				DeleteProcThreadAttributeList(attributes);
			}

			Marshal.FreeHGlobal(handles);
			Marshal.FreeHGlobal(attributes);
		}
	}

	private static IOException Failure(string what, int error) =>
		new($"{what}: {new Win32Exception(error).Message}");

	[StructLayout(LayoutKind.Sequential)]
	private struct SecurityAttributes
	{
		public int Length;
		public IntPtr SecurityDescriptor;
		public int InheritHandle;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct StartupInfo
	{
		public int Cb;
		public IntPtr Reserved;
		public IntPtr Desktop;
		public IntPtr Title;
		public int X;
		public int Y;
		public int XSize;
		public int YSize;
		public int XCountChars;
		public int YCountChars;
		public int FillAttribute;
		public int Flags;
		public short ShowWindow;
		public short Reserved2Size;
		public IntPtr Reserved2;
		public IntPtr StdInput;
		public IntPtr StdOutput;
		public IntPtr StdError;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct StartupInfoEx
	{
		public StartupInfo StartupInfo;
		public IntPtr AttributeList;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ProcessInformation
	{
		public IntPtr Process;
		public IntPtr Thread;
		public int ProcessId;
		public int ThreadId;
	}

	[DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFile(
		string fileName,
		uint desiredAccess,
		uint shareMode,
		ref SecurityAttributes securityAttributes,
		uint creationDisposition,
		uint flagsAndAttributes,
		IntPtr templateFile);

	[DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CreateProcess(
		string applicationName,
		StringBuilder commandLine,
		IntPtr processAttributes,
		IntPtr threadAttributes,
		[MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
		uint creationFlags,
		IntPtr environment,
		string currentDirectory,
		ref StartupInfoEx startupInfo,
		out ProcessInformation processInformation);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UpdateProcThreadAttribute(
		IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

	[DllImport("kernel32.dll")]
	private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseHandle(IntPtr handle);
}
