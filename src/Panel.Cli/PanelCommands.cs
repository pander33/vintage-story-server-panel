using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Panel.Fleet.Domain;
using Panel.Fleet.Infrastructure;

namespace Panel.Cli;

/// <summary>
/// Команды управления панелью из терминала.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed partial class PanelCommands
{
	private readonly LinuxHost host;

	public PanelCommands(LinuxHost host) => this.host = host;

	private TextWriter Output => this.host.Output;

	public async Task<int> StatusAsync()
	{
		if (new Installer(this.host).CurrentVersion() is { } version)
		{
			Output.WriteLine($"Panel version: {version}");
		}

		return await Shell.RunAttachedAsync("systemctl", ["status", InstallPaths.AgentUnitName, InstallPaths.PanelUnitName, "--no-pager", "-l"]).ConfigureAwait(false);
	}

	/// <summary>Запуск, остановка, перезапуск панели с ожиданием результата. Демон запускается вместе с ней.</summary>
	public async Task<int> ControlAsync(string action, int waitSeconds)
	{
		var oldPid = await this.host.UnitPropertyAsync(InstallPaths.PanelUnitName, "MainPID").ConfigureAwait(false);
		switch (action)
		{
			case "stop":
				var stopped = await this.host.SystemctlAsync("stop", InstallPaths.PanelUnitName).ConfigureAwait(false);
				Output.WriteLine(stopped.Succeeded ? "Panel stopped." : $"Panel did not stop: {stopped.Reason}");
				return stopped.Succeeded ? 0 : 1;
			case "start":
				await this.host.SystemctlAsync("start", InstallPaths.AgentUnitName).ConfigureAwait(false);
				await this.host.SystemctlAsync("start", "--no-block", InstallPaths.PanelUnitName).ConfigureAwait(false);
				return await this.host.WaitForStartAsync(InstallPaths.PanelUnitName, string.Empty, waitSeconds).ConfigureAwait(false) ? 0 : 1;
			default:
				await this.host.SystemctlAsync("restart", "--no-block", InstallPaths.PanelUnitName).ConfigureAwait(false);
				return await this.host.WaitForStartAsync(InstallPaths.PanelUnitName, oldPid, waitSeconds).ConfigureAwait(false) ? 0 : 1;
		}
	}

	/// <summary><c>logs [-f] [N]</c>: журнал юнита, по умолчанию 100 строк.</summary>
	public static async Task<int> LogsAsync(string unit, IReadOnlyList<string> arguments)
	{
		var follow = false;
		var lines = 100;
		foreach (var argument in arguments)
		{
			if (argument is "-f" or "--follow")
			{
				follow = true;
			}
			else if (int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count is > 0 and <= 100_000)
			{
				lines = count;
			}
			else
			{
				throw new InstallFailed($"Unknown option: {argument}");
			}
		}

		var journal = new List<string> { "-u", unit, "-n", lines.ToString(CultureInfo.InvariantCulture), "--no-pager" };
		if (follow)
		{
			journal.Add("-f");
		}

		return await Shell.RunAttachedAsync("journalctl", journal).ConfigureAwait(false);
	}

	/// <summary>Адрес панели, а до завершения мастера ещё и одноразовая ссылка на него.</summary>
	public async Task<int> AccessAsync()
	{
		PanelAccess.Print(Output, EnvironmentFile.Load(this.host.Paths.PanelEnv), PanelAccess.MachineAddresses(), tunnel: true);
		Output.WriteLine();

		if (this.host.SetupCompleted())
		{
			Output.WriteLine("Setup is complete — sign in with your username and password.");
			Output.WriteLine("Forgot your password: sudo vs-panel password");
			return 0;
		}

		return await this.host.ManageAsync(["claim-token"]).ConfigureAwait(false);
	}

	public async Task<int> PasswordAsync()
	{
		Output.WriteLine("The new password will be set for the admin account (it will be created if missing");
		Output.WriteLine("with the administrator role). All its previous sessions will be revoked.\n");
		return await this.host.ManageAsync(["set-password"], attached: true).ConfigureAwait(false);
	}

	public async Task<int> ServersAsync()
	{
		var servers = await LoadServersAsync().ConfigureAwait(false);
		if (servers.Count == 0)
		{
			Output.WriteLine("No servers in the registry. Create the first one in the setup wizard: sudo vs-panel access");
			return 0;
		}

		foreach (var server in servers)
		{
			var state = await this.host.UnitPropertyAsync(server.Unit, "ActiveState").ConfigureAwait(false);
			Output.WriteLine($"{server.Id.Value,-20} {Describe(state),-16} {server.Unit,-32} {server.Name}");
		}

		return 0;
	}

	/// <summary><c>server &lt;id&gt; status | start | stop | restart | logs</c>.</summary>
	public async Task<int> ServerAsync(IReadOnlyList<string> arguments)
	{
		if (arguments.Count < 2)
		{
			throw new InstallFailed("Usage: sudo vs-panel server <id> status | start | stop | restart | logs [-f] [N]");
		}

		var servers = await LoadServersAsync().ConfigureAwait(false);
		var server = servers.FirstOrDefault(item => item.Id.Value == arguments[0])
			?? throw new InstallFailed($"Server {arguments[0]} was not found in the registry. List: sudo vs-panel servers");

		// Имя юнита взято из реестра, но в systemctl передаётся только допустимое.
		if (!UnitPattern.IsMatch(server.Unit))
		{
			throw new InstallFailed($"Server {server.Id.Value} has an invalid unit name: {server.Unit}");
		}

		switch (arguments[1])
		{
			case "status":
				return await Shell.RunAttachedAsync("systemctl", ["status", server.Unit, "--no-pager", "-l"]).ConfigureAwait(false);
			case "logs":
				return await LogsAsync(server.Unit, [.. arguments.Skip(2)]).ConfigureAwait(false);
			case "start" or "stop" or "restart":
				var done = await this.host.SystemctlAsync(arguments[1], server.Unit).ConfigureAwait(false);
				var state = await this.host.UnitPropertyAsync(server.Unit, "ActiveState").ConfigureAwait(false);
				Output.WriteLine(done.Succeeded ? $"{server.Id.Value}: {Describe(state)}" : $"{server.Id.Value}: {done.Reason}");
				return done.Succeeded ? 0 : 1;
			default:
				throw new InstallFailed($"Unknown action: {arguments[1]}");
		}
	}

	private async Task<IReadOnlyList<RegisteredServer>> LoadServersAsync()
	{
		var snapshot = await new FileRegistryStore(this.host.Paths.PanelConfig).LoadAsync(CancellationToken.None).ConfigureAwait(false);
		return snapshot switch
		{
			RegistrySnapshot.Available available => available.Servers,
			RegistrySnapshot.Unavailable unavailable => throw new InstallFailed($"Could not read the server registry: {unavailable.Detail}"),
			_ => [],
		};
	}

	/// <summary>Состояние юнита по-русски.</summary>
	public static string Describe(string state) => state switch
	{
		"active" => "running",
		"activating" => "starting",
		"deactivating" => "stopping",
		"inactive" => "stopped",
		"failed" => "failed",
		"reloading" => "reloading settings",
		"" => "unknown",
		_ => state,
	};

	[GeneratedRegex(@"\A[A-Za-z0-9@._-]{1,128}\.service\z", RegexOptions.CultureInvariant)]
	private static partial Regex UnitPattern { get; }
}
