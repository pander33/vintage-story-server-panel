using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Panel.Cli;

/// <summary>Служба, которую ставит установщик на Windows.</summary>
internal sealed record ServiceDefinition(
	string Name,
	string DisplayName,
	string Description,
	string CommandLine,
	IReadOnlyList<string> Dependencies);

/// <summary>Как служба записана в диспетчере служб.</summary>
internal sealed record ServiceConfiguration(string CommandLine, string Account, bool AutoStart);

/// <summary>
/// Регистрация служб Windows: то, что на Linux делают юниты и <c>systemctl enable</c>.
/// </summary>
/// <remarks>
/// <para>
/// Через API диспетчера служб, а не <c>sc.exe</c>: описание, действия при сбое и чтение
/// настроек одним способом, с кодами ошибок Windows вместо разбора вывода.
/// </para>
/// <para>
/// Запуск и остановку выполняет <c>ServiceController</c>; здесь только то, чего он не умеет:
/// создать, изменить, удалить службу и прочитать, от чьего имени она работает.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ServiceManager
{
	private const uint managerAllAccess = 0xF003F;
	private const uint managerConnect = 0x0001;
	private const uint serviceAllAccess = 0xF01FF;
	private const uint serviceQueryConfig = 0x0001;
	private const uint win32OwnProcess = 0x10;
	private const uint autoStart = 2;
	private const uint errorNormal = 1;
	private const int serviceDoesNotExist = 1060;
	private const int insufficientBuffer = 122;
	private const uint configDescription = 1;
	private const uint configFailureActions = 2;
	private const uint configFailureActionsFlag = 4;
	private const int actionRestart = 1;

	/// <summary>Сколько ждать перед перезапуском упавшей службы.</summary>
	private static readonly TimeSpan restartDelay = TimeSpan.FromSeconds(10);

	/// <summary>Через сутки без падений счёт перезапусков начинается заново.</summary>
	private static readonly TimeSpan failureReset = TimeSpan.FromDays(1);

	public static bool Exists(string name)
	{
		using var manager = OpenManager(managerConnect);
		using var service = Open(manager, name, serviceQueryConfig);
		return service is not null;
	}

	/// <summary>
	/// Создать службу или привести существующую к описанию. Учётная запись всегда LocalSystem:
	/// службу, поставленную раньше под пользователем, переводят на неё, пароль стирается.
	/// </summary>
	public static void Install(ServiceDefinition definition)
	{
		ArgumentNullException.ThrowIfNull(definition);
		using var manager = OpenManager(managerAllAccess);
		var dependencies = string.Concat(definition.Dependencies.Select(name => name + "\0"));
		var service = Open(manager, definition.Name, serviceAllAccess);
		if (service is null)
		{
			service = CreateServiceW(
				manager,
				definition.Name,
				definition.DisplayName,
				serviceAllAccess,
				win32OwnProcess,
				autoStart,
				errorNormal,
				definition.CommandLine,
				null,
				IntPtr.Zero,
				dependencies,
				WindowsPaths.ServiceAccount,
				string.Empty);
			if (service.IsInvalid)
			{
				var error = new Win32Exception(Marshal.GetLastPInvokeError());
				service.Dispose();
				throw new InstallFailed($"Служба {definition.Name} не создана: {error.Message}");
			}
		}
		else if (!ChangeServiceConfigW(
			service,
			win32OwnProcess,
			autoStart,
			errorNormal,
			definition.CommandLine,
			null,
			IntPtr.Zero,
			dependencies,
			WindowsPaths.ServiceAccount,
			string.Empty,
			definition.DisplayName))
		{
			var error = new Win32Exception(Marshal.GetLastPInvokeError());
			service.Dispose();
			throw new InstallFailed($"Служба {definition.Name} не изменена: {error.Message}");
		}

		using (service)
		{
			Describe(service, definition);
			RestartOnFailure(service, definition.Name);
		}
	}

	/// <summary>Удалить службу. Остановить её должен вызывающий; если службы нет, ничего не делает.</summary>
	public static void Delete(string name)
	{
		using var manager = OpenManager(managerAllAccess);
		using var service = Open(manager, name, serviceAllAccess);
		if (service is not null && !DeleteService(service))
		{
			throw new InstallFailed($"Служба {name} не удалена: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
		}
	}

	/// <summary>Командная строка, учётная запись и тип запуска; <see langword="null"/>, если службы нет.</summary>
	public static ServiceConfiguration? Query(string name)
	{
		using var manager = OpenManager(managerConnect);
		using var service = Open(manager, name, serviceQueryConfig);
		if (service is null)
		{
			return null;
		}

		QueryServiceConfigW(service, IntPtr.Zero, 0, out var needed);
		if (Marshal.GetLastPInvokeError() != insufficientBuffer)
		{
			return null;
		}

		var buffer = Marshal.AllocHGlobal((int) needed);
		try
		{
			if (!QueryServiceConfigW(service, buffer, needed, out _))
			{
				return null;
			}

			var config = Marshal.PtrToStructure<QueryServiceConfig>(buffer);
			return new ServiceConfiguration(
				Marshal.PtrToStringUni(config.BinaryPathName) ?? string.Empty,
				Marshal.PtrToStringUni(config.ServiceStartName) ?? string.Empty,
				config.StartType == autoStart);
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}
	}

	private static void Describe(ServiceHandle service, ServiceDefinition definition)
	{
		var description = new ServiceDescription { Description = definition.Description };
		if (!ChangeServiceConfig2W(service, configDescription, ref description))
		{
			throw new InstallFailed($"Описание службы {definition.Name} не записано: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
		}
	}

	/// <summary>
	/// Упавшую службу диспетчер перезапускает сам, трижды, как <c>Restart=on-failure</c> в юнитах.
	/// Флаг распространяет это на выход с кодом ошибки без падения: панель без ключа сессий
	/// завершается с кодом 1.
	/// </summary>
	private static void RestartOnFailure(ServiceHandle service, string name)
	{
		var actions = new[] { actionRestart, actionRestart, actionRestart };
		var size = Marshal.SizeOf<ScAction>();
		var array = Marshal.AllocHGlobal(size * actions.Length);
		try
		{
			for (var index = 0; index < actions.Length; index++)
			{
				Marshal.StructureToPtr(new ScAction { Type = actions[index], Delay = (int) restartDelay.TotalMilliseconds }, array + (index * size), false);
			}

			var failure = new ServiceFailureActions
			{
				ResetPeriod = (int) failureReset.TotalSeconds,
				ActionCount = actions.Length,
				Actions = array,
			};
			var flag = new ServiceFailureActionsFlag { OnNonCrashFailures = 1 };
			if (!ChangeServiceConfig2W(service, configFailureActions, ref failure) || !ChangeServiceConfig2W(service, configFailureActionsFlag, ref flag))
			{
				throw new InstallFailed($"Перезапуск службы {name} при сбое не настроен: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
			}
		}
		finally
		{
			Marshal.FreeHGlobal(array);
		}
	}

	private static ServiceHandle OpenManager(uint access)
	{
		var manager = OpenSCManagerW(null, null, access);
		if (manager.IsInvalid)
		{
			var error = new Win32Exception(Marshal.GetLastPInvokeError());
			manager.Dispose();
			throw new InstallFailed($"Диспетчер служб недоступен: {error.Message}");
		}

		return manager;
	}

	private static ServiceHandle? Open(ServiceHandle manager, string name, uint access)
	{
		var service = OpenServiceW(manager, name, access);
		if (!service.IsInvalid)
		{
			return service;
		}

		var error = Marshal.GetLastPInvokeError();
		service.Dispose();
		return error == serviceDoesNotExist
			? null
			: throw new InstallFailed($"Служба {name} не открывается: {new Win32Exception(error).Message}");
	}

	private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
	{
		public ServiceHandle()
			: base(true)
		{
		}

		protected override bool ReleaseHandle() => CloseServiceHandle(handle);
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct QueryServiceConfig
	{
		public uint ServiceType;
		public uint StartType;
		public uint ErrorControl;
		public IntPtr BinaryPathName;
		public IntPtr LoadOrderGroup;
		public uint TagId;
		public IntPtr Dependencies;
		public IntPtr ServiceStartName;
		public IntPtr DisplayName;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct ServiceDescription
	{
		public string Description;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ScAction
	{
		public int Type;
		public int Delay;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ServiceFailureActions
	{
		public int ResetPeriod;
		public IntPtr RebootMessage;
		public IntPtr Command;
		public int ActionCount;
		public IntPtr Actions;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ServiceFailureActionsFlag
	{
		public int OnNonCrashFailures;
	}

	[DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern ServiceHandle OpenSCManagerW(string? machine, string? database, uint access);

	[DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern ServiceHandle OpenServiceW(ServiceHandle manager, string name, uint access);

	[DllImport("advapi32.dll", EntryPoint = "CreateServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern ServiceHandle CreateServiceW(
		ServiceHandle manager,
		string name,
		string displayName,
		uint access,
		uint serviceType,
		uint startType,
		uint errorControl,
		string binaryPath,
		string? loadOrderGroup,
		IntPtr tagId,
		string dependencies,
		string account,
		string password);

	[DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ChangeServiceConfigW(
		ServiceHandle service,
		uint serviceType,
		uint startType,
		uint errorControl,
		string binaryPath,
		string? loadOrderGroup,
		IntPtr tagId,
		string dependencies,
		string? account,
		string? password,
		string displayName);

	[DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ChangeServiceConfig2W(ServiceHandle service, uint level, ref ServiceDescription info);

	[DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ChangeServiceConfig2W(ServiceHandle service, uint level, ref ServiceFailureActions info);

	[DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool ChangeServiceConfig2W(ServiceHandle service, uint level, ref ServiceFailureActionsFlag info);

	[DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool QueryServiceConfigW(ServiceHandle service, IntPtr buffer, uint size, out uint needed);

	[DllImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DeleteService(ServiceHandle service);

	[DllImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseServiceHandle(IntPtr handle);
}
