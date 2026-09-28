using System.Runtime.Versioning;
using System.ServiceProcess;
using Panel.Fleet.Domain;
using Panel.Fleet.Infrastructure;

namespace Panel.Cli;

/// <summary>
/// Команды управления из терминала на Windows, аналог <see cref="PanelCommands"/>.
/// </summary>
/// <remarks>
/// Игровыми серверами на Windows владеет демон, а не диспетчер служб, и управлять ими можно
/// только через панель. Поэтому команды <c>server</c> здесь нет, а <c>servers</c> только
/// перечисляет реестр.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsCommands
{
	private readonly WindowsHost host;

	public WindowsCommands(WindowsHost host) => this.host = host;

	private TextWriter Output => this.host.Output;

	public int Status()
	{
		if (new WindowsInstaller(this.host).CurrentVersion() is { } version)
		{
			Output.WriteLine($"Panel version: {version}");
		}

		var healthy = true;
		foreach (var name in new[] { WindowsPaths.AgentService, WindowsPaths.PanelService })
		{
			var status = WindowsHost.Status(name);
			var account = ServiceManager.Query(name)?.Account;
			Output.WriteLine($"{name,-16} {Describe(status),-16} {account}");
			healthy &= status == ServiceControllerStatus.Running;
		}

		Output.WriteLine($"Panel address: {PanelState.LocalAddress(this.host.PanelEnvironment())}");
		return healthy ? 0 : 1;
	}

	/// <summary>Запуск, остановка, перезапуск. Демон запускается раньше панели и не останавливается вместе с ней.</summary>
	public async Task<int> ControlAsync(string action, int waitSeconds)
	{
		switch (action)
		{
			case "stop":
				return await this.host.StopAsync(WindowsPaths.PanelService, waitSeconds).ConfigureAwait(false) ? 0 : 1;
			case "start":
				return await this.host.StartAsync(WindowsPaths.AgentService, 60).ConfigureAwait(false)
					&& await this.host.StartAsync(WindowsPaths.PanelService, waitSeconds).ConfigureAwait(false) ? 0 : 1;
			default:
				if (!await this.host.StopAsync(WindowsPaths.PanelService, waitSeconds).ConfigureAwait(false))
				{
					return 1;
				}

				return await this.host.StartAsync(WindowsPaths.AgentService, 60).ConfigureAwait(false)
					&& await this.host.StartAsync(WindowsPaths.PanelService, waitSeconds).ConfigureAwait(false) ? 0 : 1;
		}
	}

	/// <summary>
	/// Журнала служб, как у journald, на Windows нет: ошибки запуска служб и сбои демона пишутся
	/// в журнал событий, вывод игровых серверов — в <c>run\&lt;id&gt;.console.log</c>.
	/// </summary>
	public int Logs()
	{
		Output.WriteLine("Panel service logs are in the panel under Panel service log and in Event Viewer:");
		Output.WriteLine("eventvwr.msc → Windows Logs → Application");
		Output.WriteLine("(sources vs-panel-agent and Panel.Web); service startup failures are under System.");
		Output.WriteLine("From PowerShell:");
		Output.WriteLine("  Get-WinEvent -LogName Application -MaxEvents 200 | Where-Object ProviderName -in 'vs-panel-agent','Panel.Web'");
		Output.WriteLine($"Game server output is in the panel Log tab and in {Path.Combine(this.host.Paths.RunDirectory, "<id>.console.log")}.");
		return 0;
	}

	/// <summary>Адрес панели, а до завершения мастера — одноразовая ссылка на него, открытая в браузере.</summary>
	public async Task<int> AccessAsync(bool openBrowser)
	{
		var address = PanelState.LocalAddress(this.host.PanelEnvironment());
		PanelAccess.Print(Output, this.host.PanelEnvironment(), PanelAccess.MachineAddresses(), tunnel: false);
		Output.WriteLine();
		if (this.host.SetupCompleted())
		{
			Output.WriteLine("Setup is complete — sign in with your username and password.");
			Output.WriteLine("Forgot your password: vs-panel password");
			if (openBrowser)
			{
				await this.host.OpenAsync(address).ConfigureAwait(false);
			}

			return 0;
		}

		var claim = await this.host.ManageAsync(["claim-token"]).ConfigureAwait(false);
		if (openBrowser && PanelState.ClaimLink(claim.Output) is { } link)
		{
			await this.host.OpenAsync(link).ConfigureAwait(false);
		}

		return claim.ExitCode;
	}

	public async Task<int> PasswordAsync()
	{
		Output.WriteLine("The new password will be set for the admin account (it will be created if missing");
		Output.WriteLine("with the administrator role). All its previous sessions will be revoked.\n");
		return await this.host.ManageAttachedAsync(["set-password"]).ConfigureAwait(false);
	}

	public async Task<int> ServersAsync()
	{
		var snapshot = await new FileRegistryStore(this.host.Paths.PanelConfig).LoadAsync(CancellationToken.None).ConfigureAwait(false);
		IReadOnlyList<RegisteredServer> servers = snapshot switch
		{
			RegistrySnapshot.Available available => available.Servers,
			RegistrySnapshot.Unavailable unavailable => throw new InstallFailed($"Could not read the server registry: {unavailable.Detail}"),
			_ => [],
		};

		if (servers.Count == 0)
		{
			Output.WriteLine("No servers in the registry. Create the first one in the setup wizard: vs-panel access");
			return 0;
		}

		foreach (var server in servers)
		{
			Output.WriteLine($"{server.Id.Value,-20} {server.Name}");
		}

		Output.WriteLine("\nOn Windows the agent manages game servers: start, stop and logs are in the panel.");
		return 0;
	}

	public static string Describe(ServiceControllerStatus? status) => status switch
	{
		ServiceControllerStatus.Running => "running",
		ServiceControllerStatus.StartPending => "starting",
		ServiceControllerStatus.StopPending => "stopping",
		ServiceControllerStatus.Stopped => "stopped",
		ServiceControllerStatus.Paused => "paused",
		null => "not installed",
		_ => status.Value.ToString(),
	};
}
