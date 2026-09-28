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
		Usage: sudo vs-panel <command>

		Installation:
		  install --from <directory>   install the panel from a build directory (usually via sh install.sh)
		          [--no-server] [--skip-dotnet] [--skip-packages] [--servers-dir <directory>]
		  update --from <directory>    install a new version; retain the previous two for rollback
		  rollback                     restore the previous version
		  repair                       rewrite units, console wrapper, permissions and active-version links
		  doctor                       check the installation without changing it
		  uninstall [--purge] [--yes]
		                               remove panel services; --purge also removes the program,
		                               state, settings and the vs-panel user
		  version                      this command's version and the active panel version

		Panel access options for install, update and repair; omitted options keep their values:
		  --access localhost|lan|internet
		                               localhost: this machine only (SSH tunnel); lan: all interfaces
		                               and hostnames, port open to the local network (default);
		                               internet: all interfaces, port open to everyone
		  --bind <address>  --port <port>  --allowed-hosts <comma-separated names, * for any>
		  --secure-cookie true|false    HTTPS-only cookies (requires an HTTPS proxy)
		  --firewall lan|internet|off   who may reach the panel port through ufw; off: manage rules yourself
		  Environment equivalents: VS_PANEL_ACCESS, VS_PANEL_BIND, VS_PANEL_PORT,
		  VS_PANEL_ALLOWED_HOSTS, VS_PANEL_SECURE_COOKIE, VS_PANEL_FIREWALL.
		  Example: sudo vs-panel repair --access internet

		Panel:
		  status                       daemon and panel status
		  start | stop | restart       start, stop or restart the panel and wait for the result
		  logs [-f] [N]                panel log (default: 100 lines); -f follows new entries
		  agent-logs [-f] [N]          daemon log
		  access                       panel address; setup link until the wizard is complete
		  password                     set a new password for the admin account

		Game servers:
		  servers                      list servers and their status
		  server <id> status | start | stop | restart | logs [-f] [N]

		Installation variables (also used by install.sh): VS_PANEL_CREATE_SERVER=0,
		VS_PANEL_GAME_VERSION, VS_PANEL_GAME_PORT, VS_PANEL_INSTALL_WAIT_SECONDS,
		VS_PANEL_DOTNET_CHANNEL, VS_PANEL_SKIP_DOTNET=1, VS_TELEMETRY_MOD_DIR;
		VS_PANEL_SERVERS_DIR is equivalent to --servers-dir. Without it, the first terminal
		installation prompts for a server directory. Change it later under Settings → Panel.

		Uninstalling the panel does not affect game servers.
		""";

	public const string WindowsUsage = """
		Usage: vs-panel <command>   (in an Administrator command prompt)

		Installation:
		  install --from <directory>   install the panel from a build directory (usually via install.cmd)
		          [--no-server] [--skip-dotnet] [--no-browser] [--servers-dir <directory>]
		                               --servers-dir selects where game servers are created; the first
		                               installation prompts for it if omitted; change it later in the panel
		  update --from <directory>    install a new version; retain the previous two for rollback
		  rollback                     restore the previous version
		  repair                       re-register services, permissions and the active-version link
		  doctor                       check the installation without changing it
		  uninstall [--purge] [--yes]
		                               remove panel services; --purge also removes the program,
		                               state and settings
		  version                      this command's version and the active panel version

		Panel access options for install, update and repair; omitted options keep their values:
		  --access localhost|lan|internet
		                               localhost: this machine only (SSH tunnel); lan: all interfaces
		                               and hostnames, port open to the local network (default);
		                               internet: all interfaces, port open to everyone
		  --bind <address>  --port <port>  --allowed-hosts <comma-separated names, * for any>
		  --secure-cookie true|false    HTTPS-only cookies (requires an HTTPS proxy)
		  --firewall lan|internet|off   who may reach the panel port through Windows Firewall; off: manage rules yourself
		  Environment equivalents: VS_PANEL_ACCESS, VS_PANEL_BIND, VS_PANEL_PORT,
		  VS_PANEL_ALLOWED_HOSTS, VS_PANEL_SECURE_COOKIE, VS_PANEL_FIREWALL.
		  Example: vs-panel repair --access internet

		Panel:
		  status                       daemon and panel service status
		  start | stop | restart       start, stop or restart the panel and wait for the result
		  logs                         where to find the service log
		  access [--no-browser]        open the panel; setup link until the wizard is complete
		  password                     set a new password for the admin account
		  servers                      list servers from the registry

		The vs-panel-agent and vs-panel services run as LocalSystem: no password is needed and
		the user account is unchanged. The program is in Program Files\vs-panel and data is in
		ProgramData\vs-panel.

		Installation variables: VS_PANEL_CREATE_SERVER=0, VS_PANEL_GAME_VERSION, VS_PANEL_GAME_PORT,
		VS_PANEL_INSTALL_WAIT_SECONDS, VS_PANEL_DOTNET_CHANNEL, VS_PANEL_SKIP_DOTNET=1.

		Uninstalling the panel does not affect game servers.
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

			await Console.Error.WriteLineAsync("vs-panel supports Linux and Windows.").ConfigureAwait(false);
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
			throw new InstallFailed($"Run as root: sudo vs-panel {command}");
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
					throw new InstallFailed($"Specify the build directory: sudo vs-panel {command} --from <directory>");
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
				Console.WriteLine($"Active panel version: {installer.CurrentVersion() ?? "not installed"}");
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
			throw new InstallFailed($"Administrator privileges required: open an Administrator command prompt and retry vs-panel {command}");
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
					throw new InstallFailed($"Specify the build directory: vs-panel {command} --from <directory>");
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
				Console.WriteLine($"Active panel version: {installer.CurrentVersion() ?? "not installed"}");
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
				throw new InstallFailed("On Windows, the daemon manages game servers. Start, stop and log controls are in the panel (vs-panel access).");
			default:
				await Console.Error.WriteLineAsync(WindowsUsage).ConfigureAwait(false);
				return 2;
		}
	}

	private static T Require<T>(Result<T> result) =>
		result.IsSuccess ? result.Value : throw new InstallFailed(result.Failure!.Message);
}
