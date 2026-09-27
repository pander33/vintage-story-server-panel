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
			Output.WriteLine($"Версия панели: {version}");
		}

		var healthy = true;
		foreach (var name in new[] { WindowsPaths.AgentService, WindowsPaths.PanelService })
		{
			var status = WindowsHost.Status(name);
			var account = ServiceManager.Query(name)?.Account;
			Output.WriteLine($"{name,-16} {Describe(status),-16} {account}");
			healthy &= status == ServiceControllerStatus.Running;
		}

		Output.WriteLine($"Адрес панели: {PanelState.LocalAddress(this.host.PanelEnvironment())}");
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
		Output.WriteLine("Журнал служб панели — раздел «Журнал службы панели» в панели; он же в журнале событий:");
		Output.WriteLine("eventvwr.msc → Журналы Windows → Приложение");
		Output.WriteLine("(источники vs-panel-agent и Panel.Web), отказы запуска служб — там же в разделе «Система».");
		Output.WriteLine("Из PowerShell:");
		Output.WriteLine("  Get-WinEvent -LogName Application -MaxEvents 200 | Where-Object ProviderName -in 'vs-panel-agent','Panel.Web'");
		Output.WriteLine($"Вывод игровых серверов — раздел «Журнал» панели и файлы {Path.Combine(this.host.Paths.RunDirectory, "<id>.console.log")}.");
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
			Output.WriteLine("Мастер установки завершён — вход обычным логином и паролем.");
			Output.WriteLine("Забыли пароль: vs-panel password");
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
		Output.WriteLine("Новый пароль будет задан учётной записи admin (если её нет, она будет создана");
		Output.WriteLine("с ролью администратора). Все её прежние сессии будут завершены.\n");
		return await this.host.ManageAttachedAsync(["set-password"]).ConfigureAwait(false);
	}

	public async Task<int> ServersAsync()
	{
		var snapshot = await new FileRegistryStore(this.host.Paths.PanelConfig).LoadAsync(CancellationToken.None).ConfigureAwait(false);
		IReadOnlyList<RegisteredServer> servers = snapshot switch
		{
			RegistrySnapshot.Available available => available.Servers,
			RegistrySnapshot.Unavailable unavailable => throw new InstallFailed($"Реестр серверов не прочитан: {unavailable.Detail}"),
			_ => [],
		};

		if (servers.Count == 0)
		{
			Output.WriteLine("Серверов в реестре нет. Создайте первый в мастере установки: vs-panel access");
			return 0;
		}

		foreach (var server in servers)
		{
			Output.WriteLine($"{server.Id.Value,-20} {server.Name}");
		}

		Output.WriteLine("\nНа Windows серверами управляет демон: запуск, остановка и журнал — в панели.");
		return 0;
	}

	public static string Describe(ServiceControllerStatus? status) => status switch
	{
		ServiceControllerStatus.Running => "работает",
		ServiceControllerStatus.StartPending => "запускается",
		ServiceControllerStatus.StopPending => "останавливается",
		ServiceControllerStatus.Stopped => "остановлена",
		ServiceControllerStatus.Paused => "приостановлена",
		null => "не установлена",
		_ => status.Value.ToString(),
	};
}
