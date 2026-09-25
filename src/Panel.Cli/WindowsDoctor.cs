using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;

namespace Panel.Cli;

/// <summary>
/// <c>vs-panel doctor</c> на Windows: что установлено, что запущено, что осталось от прежней
/// версии панели. Ничего не меняет; что делать, написано в тексте находки.
/// </summary>
/// <remarks>
/// Как и в <see cref="Doctor"/>, файловые проверки отделены от проверок служб: первые
/// тестируются на любом каталоге, вторые обращаются к диспетчеру служб.
/// </remarks>
internal static class WindowsDoctor
{
	private const string command = "vs-panel";

	public static IReadOnlyList<Finding> CheckFiles(WindowsPaths paths, string stateDirectory)
	{
		ArgumentNullException.ThrowIfNull(paths);
		var findings = new List<Finding>();

		var version = FirstLine(Path.Combine(paths.Current, Artifact.VersionFile));
		if (version is null)
		{
			findings.Add(new(Severity.Problem, $"Панель не установлена: нет {Path.Combine(paths.Current, Artifact.VersionFile)}. Установите: install.cmd из каталога сборки"));
		}
		else
		{
			var missing = Artifact.Binaries
				.Select(binary => binary.Installed + Artifact.Suffix(windows: true))
				.Where(name => !File.Exists(Path.Combine(paths.Current, name)))
				.ToList();
			findings.Add(missing.Count == 0
				? new(Severity.Ok, $"Активная версия {version}")
				: new(Severity.Problem, $"В версии {version} нет {string.Join(", ", missing)}: переустановите её (install.cmd из каталога сборки)"));
		}

		findings.Add(Doctor.CheckPanelConfig(paths.PanelConfig));
		findings.Add(Doctor.CheckRegistry(paths.Registry));
		findings.Add(File.Exists(paths.PanelEnv)
			? new(Severity.Ok, $"Файл окружения: {paths.PanelEnv}")
			: new(Severity.Problem, $"Нет файла окружения {paths.PanelEnv}. Исправит {command} repair"));
		findings.AddRange(Doctor.CheckState(stateDirectory, command));

		findings.Add(Directory.Exists(paths.TelemetryDirectory) && Directory.GetFiles(paths.TelemetryDirectory, "vspaneltelemetry_*.zip").Length > 0
			? new(Severity.Ok, $"Мод телеметрии для новых серверов: {paths.TelemetryDirectory}")
			: new(Severity.Warning, $"Нет мода телеметрии в {paths.TelemetryDirectory}: готовность новых серверов проверяется ограниченно"));

		var leftovers = paths.LegacyProgram.Where(path => File.Exists(path) || Directory.Exists(path)).ToList();
		if (leftovers.Count > 0)
		{
			findings.Add(new(Severity.Warning, $"Остались файлы прежней версии панели: {string.Join(", ", leftovers)}. Новая панель их не использует; удалить можно вручную"));
		}

		return findings;
	}

	[SupportedOSPlatform("windows")]
	public static async Task<IReadOnlyList<Finding>> CheckServicesAsync(WindowsHost host)
	{
		ArgumentNullException.ThrowIfNull(host);
		var findings = new List<Finding>();
		foreach (var (name, expected) in new[] { (WindowsPaths.AgentService, host.Paths.AgentCommandLine), (WindowsPaths.PanelService, host.Paths.PanelCommandLine) })
		{
			var config = ServiceManager.Query(name);
			if (config is null)
			{
				findings.Add(new(Severity.Problem, $"Служба {name} не установлена. Исправит {command} repair"));
				continue;
			}

			if (!WindowsPaths.IsServiceAccount(config.Account))
			{
				findings.Add(new(Severity.Problem, $"{name} работает не от LocalSystem, а от {config.Account}. Исправит {command} repair"));
			}

			var status = WindowsHost.Status(name);
			findings.Add(status == ServiceControllerStatus.Running && config.AutoStart
				? new(Severity.Ok, $"{name}: работает, запуск автоматический, учётная запись {config.Account}")
				: new(Severity.Problem, $"{name}: {WindowsCommands.Describe(status)}{(config.AutoStart ? string.Empty : ", запуск не автоматический")}. Запустить: {command} start; причина — {command} logs"));
			if (!string.Equals(config.CommandLine, expected, StringComparison.OrdinalIgnoreCase))
			{
				findings.Add(new(Severity.Problem, $"{name} запускает не активную версию: {config.CommandLine}. Исправит {command} repair"));
			}
		}

		findings.Add(PipeExists(WindowsPaths.AgentPipe)
			? new(Severity.Ok, $"Канал демона \\\\.\\pipe\\{WindowsPaths.AgentPipe}")
			: new(Severity.Problem, $"Нет канала демона \\\\.\\pipe\\{WindowsPaths.AgentPipe}: панель не может выполнять команды"));

		findings.Add(CheckDataAccess(host.Paths.DataRoot));
		findings.Add(await CheckPanelPortAsync(host).ConfigureAwait(false));

		foreach (var task in WindowsPaths.LegacyTasks)
		{
			if ((await Shell.RunAsync("schtasks.exe", ["/Query", "/TN", task]).ConfigureAwait(false)).Succeeded)
			{
				findings.Add(new(Severity.Problem, $"Осталась задача планировщика прежней версии {task}: она займёт порт панели при загрузке. Удалит {command} repair"));
			}
		}

		var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
		var runtimes = File.Exists(dotnet) ? await Shell.RunAsync(dotnet, ["--list-runtimes"]).ConfigureAwait(false) : null;
		findings.Add(runtimes is { Succeeded: true } && runtimes.Output.Contains("Microsoft.NETCore.App 10.", StringComparison.Ordinal)
			? new(Severity.Ok, "Среда .NET 10 для игры установлена")
			: new(Severity.Warning, $"Не найдена среда .NET 10 в {Path.GetDirectoryName(dotnet)}: серверы Vintage Story 1.22 не запустятся"));
		return findings;
	}

	/// <summary>Каталог данных не должен быть доступен на чтение всем: там ключ сессий и учётные записи.</summary>
	[SupportedOSPlatform("windows")]
	private static Finding CheckDataAccess(string dataRoot)
	{
		if (!Directory.Exists(dataRoot))
		{
			return new(Severity.Problem, $"Нет каталога данных {dataRoot}. Исправит {command} repair");
		}

		SecurityIdentifier[] broad =
		[
			new(WellKnownSidType.BuiltinUsersSid, null),
			new(WellKnownSidType.AuthenticatedUserSid, null),
			new(WellKnownSidType.WorldSid, null),
		];
		var rules = new DirectoryInfo(dataRoot).GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
		var open = rules.OfType<FileSystemAccessRule>()
			.Where(rule => rule.AccessControlType == AccessControlType.Allow && broad.Contains(rule.IdentityReference))
			.Select(rule => rule.IdentityReference.Translate(typeof(NTAccount)).Value)
			.Distinct(StringComparer.Ordinal)
			.ToList();
		return open.Count == 0
			? new(Severity.Ok, $"Каталог данных {dataRoot} закрыт от остальных пользователей")
			: new(Severity.Problem, $"Каталог данных {dataRoot} доступен {string.Join(", ", open)}: ключ сессий может прочитать любой пользователь машины. Исправит {command} repair");
	}

	[SupportedOSPlatform("windows")]
	private static async Task<Finding> CheckPanelPortAsync(WindowsHost host)
	{
		var address = PanelState.LocalAddress(host.PanelEnvironment());
		var uri = new Uri(address);
		using var client = new TcpClient();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
		try
		{
			await client.ConnectAsync(uri.Host, uri.Port, timeout.Token).ConfigureAwait(false);
			return new(Severity.Ok, $"Панель отвечает на {address}");
		}
		catch (Exception exception) when (exception is SocketException or OperationCanceledException)
		{
			return new(Severity.Problem, $"Панель не отвечает на {address}. Причина — {command} logs");
		}
	}

	private static bool PipeExists(string name)
	{
		try
		{
			return Directory.GetFiles(@"\\.\pipe\").Any(pipe => string.Equals(Path.GetFileName(pipe), name, StringComparison.OrdinalIgnoreCase));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return false;
		}
	}

	private static string? FirstLine(string path)
	{
		try
		{
			return File.ReadLines(path).FirstOrDefault()?.Trim();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}
}
