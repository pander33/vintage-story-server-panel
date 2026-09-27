using System.Collections;
using System.Reflection;
using Panel.SharedKernel;

namespace Panel.Cli;

/// <summary>
/// <c>vs-panel</c>: установщик и управление панелью из терминала.
/// </summary>
/// <remarks>
/// На Linux это юниты systemd от root, на Windows — службы от LocalSystem и командная строка
/// администратора.
/// </remarks>
public static class Program
{
	public const string Usage = """
		Использование: sudo vs-panel <команда>

		Установка:
		  install --from <каталог>   установить панель из каталога сборки (обычно через sh install.sh)
		          [--no-server] [--skip-dotnet] [--skip-packages] [--servers-dir <каталог>]
		  update --from <каталог>    установить новую версию; предыдущие две хранятся для отката
		  rollback                   вернуть предыдущую версию
		  repair                     переписать юниты, обёртку, права и ссылки активной версии
		  doctor                     проверить установку, ничего не меняя
		  uninstall [--purge] [--yes]
		                             удалить службы панели; --purge удаляет также программу,
		                             состояние, настройки и пользователя vs-panel
		  version                    версия этой команды и активная версия панели

		Доступ к панели — при install, update и repair; не заданное не меняется:
		  --access localhost|lan|internet
		                             localhost — только эта машина (SSH-туннель); lan — все
		                             интерфейсы и любое имя, порт открыт сети (умолчание);
		                             internet — все интерфейсы, порт открыт всем
		  --bind <адрес>  --port <порт>  --allowed-hosts <имена через запятую, * — любые>
		  --secure-cookie true|false  куки только по HTTPS (нужен HTTPS-прокси перед панелью)
		  --firewall lan|internet|off кому открыть порт панели в ufw; off — правила ведёте вы
		  То же переменными: VS_PANEL_ACCESS, VS_PANEL_BIND, VS_PANEL_PORT,
		  VS_PANEL_ALLOWED_HOSTS, VS_PANEL_SECURE_COOKIE, VS_PANEL_FIREWALL.
		  Пример: sudo vs-panel repair --access internet

		Панель:
		  status                     состояние демона и панели
		  start | stop | restart     запуск, остановка, перезапуск панели с ожиданием результата
		  logs [-f] [N]              журнал панели (по умолчанию 100 строк), -f — следить
		  agent-logs [-f] [N]        журнал демона
		  access                     адрес панели; до завершения мастера — ссылка мастера установки
		  password                   новый пароль учётной записи admin

		Игровые серверы:
		  servers                    список серверов и их состояние
		  server <id> status | start | stop | restart | logs [-f] [N]

		Переменные установки — как у install.sh: VS_PANEL_CREATE_SERVER=0,
		VS_PANEL_GAME_VERSION, VS_PANEL_GAME_PORT, VS_PANEL_INSTALL_WAIT_SECONDS,
		VS_PANEL_DOTNET_CHANNEL, VS_PANEL_SKIP_DOTNET=1, VS_TELEMETRY_MOD_DIR; новая —
		VS_PANEL_SERVERS_DIR (то же, что --servers-dir). Без неё первая установка в терминале
		спрашивает каталог серверов; сменить его можно и в панели: «Настройки» → «Панель».

		Игровые серверы при удалении панели не затрагиваются.
		""";

	public const string WindowsUsage = """
		Использование: vs-panel <команда>   (в командной строке администратора)

		Установка:
		  install --from <каталог>   установить панель из каталога сборки (обычно через install.cmd)
		          [--no-server] [--skip-dotnet] [--no-browser] [--servers-dir <каталог>]
		                             --servers-dir — где создавать игровые серверы; без него
		                             первая установка спросит, а сменить можно в панели
		  update --from <каталог>    установить новую версию; предыдущие две хранятся для отката
		  rollback                   вернуть предыдущую версию
		  repair                     перерегистрировать службы, права и ссылку активной версии
		  doctor                     проверить установку, ничего не меняя
		  uninstall [--purge] [--yes]
		                             удалить службы панели; --purge удаляет также программу,
		                             состояние и настройки
		  version                    версия этой команды и активная версия панели

		Доступ к панели — при install, update и repair; не заданное не меняется:
		  --access localhost|lan|internet
		                             localhost — только эта машина (SSH-туннель); lan — все
		                             интерфейсы и любое имя, порт открыт сети (умолчание);
		                             internet — все интерфейсы, порт открыт всем
		  --bind <адрес>  --port <порт>  --allowed-hosts <имена через запятую, * — любые>
		  --secure-cookie true|false  куки только по HTTPS (нужен HTTPS-прокси перед панелью)
		  --firewall lan|internet|off кому открыть порт панели в брандмауэре Windows; off — правила ведёте вы
		  То же переменными: VS_PANEL_ACCESS, VS_PANEL_BIND, VS_PANEL_PORT,
		  VS_PANEL_ALLOWED_HOSTS, VS_PANEL_SECURE_COOKIE, VS_PANEL_FIREWALL.
		  Пример: vs-panel repair --access internet

		Панель:
		  status                     состояние служб демона и панели
		  start | stop | restart     запуск, остановка, перезапуск панели с ожиданием результата
		  logs                       где искать журнал служб
		  access [--no-browser]      открыть панель; до завершения мастера — ссылка мастера установки
		  password                   новый пароль учётной записи admin
		  servers                    список серверов из реестра

		Службы vs-panel-agent и vs-panel работают от LocalSystem: пароль не нужен, учётная
		запись пользователя не меняется. Программа — в Program Files\vs-panel, данные —
		в ProgramData\vs-panel.

		Переменные установки: VS_PANEL_CREATE_SERVER=0, VS_PANEL_GAME_VERSION, VS_PANEL_GAME_PORT,
		VS_PANEL_INSTALL_WAIT_SECONDS, VS_PANEL_DOTNET_CHANNEL, VS_PANEL_SKIP_DOTNET=1.

		Игровые серверы при удалении панели не затрагиваются.
		""";

	public static async Task<int> Main(string[] args)
	{
		if (OperatingSystem.IsWindows())
		{
			// Консоль Windows по умолчанию использует кодовую страницу OEM, и без этого русский текст
			// выводится знаками вопроса.
			Console.OutputEncoding = System.Text.Encoding.UTF8;
			Console.InputEncoding = System.Text.Encoding.UTF8;
		}

		var command = args.Length == 0 ? "help" : args[0];
		var rest = args.Skip(1).ToList();
		if (command is "help" or "-h" or "--help")
		{
			Console.WriteLine(OperatingSystem.IsWindows() ? WindowsUsage : Usage);
			return 0;
		}

		if (command is "version" or "--version")
		{
			Console.WriteLine($"vs-panel {OwnVersion}");
		}

		if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
		{
			if (command is "version" or "--version")
			{
				return 0;
			}

			await Console.Error.WriteLineAsync("vs-panel работает на Linux и Windows.").ConfigureAwait(false);
			return 2;
		}

		try
		{
			if (OperatingSystem.IsWindows())
			{
				return await RunWindowsAsync(command, rest).ConfigureAwait(false);
			}

			return OperatingSystem.IsLinux() ? await RunLinuxAsync(command, rest).ConfigureAwait(false) : 2;
		}
		catch (InstallFailed failure)
		{
			await Console.Error.WriteLineAsync(failure.Message).ConfigureAwait(false);
			return 1;
		}
	}

	/// <summary>Версия этой сборки: <c>PanelVersion</c> из Directory.Build.props.</summary>
	public static string OwnVersion =>
		typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
		?? "unknown";

	[System.Runtime.Versioning.SupportedOSPlatform("linux")]
	private static async Task<int> RunLinuxAsync(string command, List<string> rest)
	{
		var paths = InstallPaths.System;
		var host = new LinuxHost(paths, Console.Out);
		var needsRoot = command is not ("status" or "logs" or "agent-logs" or "version" or "--version");
		if (needsRoot && !LinuxHost.IsRoot)
		{
			throw new InstallFailed($"Запустите от root: sudo vs-panel {command}");
		}

		var environment = Environment.GetEnvironmentVariables()
			.Cast<DictionaryEntry>()
			.ToDictionary(entry => (string) entry.Key, entry => entry.Value as string ?? string.Empty, StringComparer.Ordinal);
		var installer = new Installer(host);
		var commands = new PanelCommands(host);

		switch (command)
		{
			case "install" or "update":
				var options = Require(InstallOptions.Parse(rest, environment, paths));
				if (options.From.Length == 0)
				{
					throw new InstallFailed($"Укажите каталог сборки: sudo vs-panel {command} --from <каталог>");
				}

				return await installer.InstallAsync(options).ConfigureAwait(false);
			case "repair":
				var repair = Require(InstallOptions.Parse(rest, environment, paths)) with { From = paths.Current, Repair = true };
				return await installer.InstallAsync(repair).ConfigureAwait(false);
			case "rollback":
				return await installer.RollbackAsync(Require(InstallOptions.Parse([], environment, paths)).WaitSeconds).ConfigureAwait(false);
			case "doctor":
				var findings = Doctor.CheckFiles(paths, host.StateDirectory()).Concat(await Doctor.CheckServicesAsync(host).ConfigureAwait(false));
				return Doctor.Print(findings, Console.Out);
			case "uninstall":
				return await new Uninstaller(host).RunAsync(rest).ConfigureAwait(false);
			case "version" or "--version":
				Console.WriteLine($"Активная версия панели: {installer.CurrentVersion() ?? "не установлена"}");
				return 0;
			case "status":
				return await commands.StatusAsync().ConfigureAwait(false);
			case "start" or "stop" or "restart":
				return await commands.ControlAsync(command, Require(InstallOptions.Parse([], environment, paths)).WaitSeconds).ConfigureAwait(false);
			case "logs":
				return await PanelCommands.LogsAsync(InstallPaths.PanelUnitName, rest).ConfigureAwait(false);
			case "agent-logs":
				return await PanelCommands.LogsAsync(InstallPaths.AgentUnitName, rest).ConfigureAwait(false);
			case "access" or "claim":
				return await commands.AccessAsync().ConfigureAwait(false);
			case "password":
				return await commands.PasswordAsync().ConfigureAwait(false);
			case "servers":
				return await commands.ServersAsync().ConfigureAwait(false);
			case "server":
				return await commands.ServerAsync(rest).ConfigureAwait(false);
			default:
				await Console.Error.WriteLineAsync(Usage).ConfigureAwait(false);
				return 2;
		}
	}

	/// <summary>
	/// Windows: службы и каталог программ требуют прав администратора, как root на Linux. Без
	/// этих прав работают только команды, которые ничего не меняют.
	/// </summary>
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
	private static async Task<int> RunWindowsAsync(string command, List<string> rest)
	{
		var paths = WindowsPaths.System;
		var host = new WindowsHost(paths, Console.Out);
		var needsAdministrator = command is not ("status" or "logs" or "agent-logs" or "version" or "--version" or "servers");
		if (needsAdministrator && !WindowsAccount.IsAdministrator())
		{
			throw new InstallFailed($"Нужны права администратора: откройте командную строку «От имени администратора» и повторите vs-panel {command}");
		}

		var environment = Environment.GetEnvironmentVariables()
			.Cast<DictionaryEntry>()
			.ToDictionary(entry => (string) entry.Key, entry => entry.Value as string ?? string.Empty, StringComparer.OrdinalIgnoreCase);
		var installer = new WindowsInstaller(host);
		var commands = new WindowsCommands(host);

		switch (command)
		{
			case "install" or "update":
				var options = Require(InstallOptions.ParseWindows(rest, environment));
				if (options.From.Length == 0)
				{
					throw new InstallFailed($"Укажите каталог сборки: vs-panel {command} --from <каталог>");
				}

				return await installer.InstallAsync(options).ConfigureAwait(false);
			case "repair":
				var repair = Require(InstallOptions.ParseWindows(rest, environment)) with { From = paths.Current, Repair = true };
				return await installer.InstallAsync(repair).ConfigureAwait(false);
			case "rollback":
				return await installer.RollbackAsync(Require(InstallOptions.ParseWindows([], environment)).WaitSeconds).ConfigureAwait(false);
			case "doctor":
				var findings = WindowsDoctor.CheckFiles(paths, host.StateDirectory()).Concat(await WindowsDoctor.CheckServicesAsync(host).ConfigureAwait(false));
				return Doctor.Print(findings, Console.Out);
			case "uninstall":
				return await new WindowsUninstaller(host).RunAsync(rest).ConfigureAwait(false);
			case "version" or "--version":
				Console.WriteLine($"Активная версия панели: {installer.CurrentVersion() ?? "не установлена"}");
				return 0;
			case "status":
				return commands.Status();
			case "start" or "stop" or "restart":
				return await commands.ControlAsync(command, Require(InstallOptions.ParseWindows([], environment)).WaitSeconds).ConfigureAwait(false);
			case "logs" or "agent-logs":
				return commands.Logs();
			case "access" or "claim":
				return await commands.AccessAsync(openBrowser: !rest.Contains("--no-browser")).ConfigureAwait(false);
			case "password":
				return await commands.PasswordAsync().ConfigureAwait(false);
			case "servers":
				return await commands.ServersAsync().ConfigureAwait(false);
			case "server":
				throw new InstallFailed("На Windows серверами управляет демон: запуск, остановка и журнал сервера — в панели (vs-panel access).");
			default:
				await Console.Error.WriteLineAsync(WindowsUsage).ConfigureAwait(false);
				return 2;
		}
	}

	private static T Require<T>(Result<T> result) =>
		result.IsSuccess ? result.Value : throw new InstallFailed(result.Failure!.Message);
}
