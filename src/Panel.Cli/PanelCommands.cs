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
			Output.WriteLine($"Версия панели: {version}");
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
				Output.WriteLine(stopped.Succeeded ? "Панель остановлена." : $"Панель не остановлена: {stopped.Reason}");
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
				throw new InstallFailed($"Неизвестный параметр: {argument}");
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
		var environment = EnvironmentFile.Load(this.host.Paths.PanelEnv);
		var bind = EnvironmentFile.Value(environment, "PANEL_BIND", "127.0.0.1");
		var port = EnvironmentFile.Value(environment, "PANEL_PORT", "8080");
		if (bind is "127.0.0.1" or "localhost" or "::1")
		{
			Output.WriteLine($"Панель слушает только эту машину: {bind}:{port}");
			Output.WriteLine($"С рабочего компьютера откройте туннель и затем http://127.0.0.1:{port}/");
			Output.WriteLine($"  ssh -L {port}:127.0.0.1:{port} {Environment.GetEnvironmentVariable("SUDO_USER") ?? "user"}@{Environment.MachineName}\n");
		}
		else
		{
			Output.WriteLine($"Панель опубликована: http://{bind}:{port}/\n");
		}

		if (this.host.SetupCompleted())
		{
			Output.WriteLine("Мастер установки завершён — вход обычным логином и паролем.");
			Output.WriteLine("Забыли пароль: sudo vs-panel password");
			return 0;
		}

		return await this.host.ManageAsync(["claim-token"]).ConfigureAwait(false);
	}

	public async Task<int> PasswordAsync()
	{
		Output.WriteLine("Новый пароль будет задан учётной записи admin (если её нет, она будет создана");
		Output.WriteLine("с ролью администратора). Все её прежние сессии будут завершены.\n");
		return await this.host.ManageAsync(["set-password"], attached: true).ConfigureAwait(false);
	}

	public async Task<int> ServersAsync()
	{
		var servers = await LoadServersAsync().ConfigureAwait(false);
		if (servers.Count == 0)
		{
			Output.WriteLine("Серверов в реестре нет. Создайте первый в мастере установки: sudo vs-panel access");
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
			throw new InstallFailed("Использование: sudo vs-panel server <id> status | start | stop | restart | logs [-f] [N]");
		}

		var servers = await LoadServersAsync().ConfigureAwait(false);
		var server = servers.FirstOrDefault(item => item.Id.Value == arguments[0])
			?? throw new InstallFailed($"Сервер {arguments[0]} не найден в реестре. Список: sudo vs-panel servers");

		// Имя юнита взято из реестра, но в systemctl передаётся только допустимое.
		if (!UnitPattern.IsMatch(server.Unit))
		{
			throw new InstallFailed($"У сервера {server.Id.Value} недопустимое имя юнита: {server.Unit}");
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
				throw new InstallFailed($"Неизвестное действие: {arguments[1]}");
		}
	}

	private async Task<IReadOnlyList<RegisteredServer>> LoadServersAsync()
	{
		var snapshot = await new FileRegistryStore(this.host.Paths.PanelConfig).LoadAsync(CancellationToken.None).ConfigureAwait(false);
		return snapshot switch
		{
			RegistrySnapshot.Available available => available.Servers,
			RegistrySnapshot.Unavailable unavailable => throw new InstallFailed($"Реестр серверов не прочитан: {unavailable.Detail}"),
			_ => [],
		};
	}

	/// <summary>Состояние юнита по-русски.</summary>
	public static string Describe(string state) => state switch
	{
		"active" => "работает",
		"activating" => "запускается",
		"deactivating" => "останавливается",
		"inactive" => "остановлена",
		"failed" => "ошибка",
		"reloading" => "перечитывает настройки",
		"" => "неизвестно",
		_ => state,
	};

	[GeneratedRegex(@"\A[A-Za-z0-9@._-]{1,128}\.service\z", RegexOptions.CultureInvariant)]
	private static partial Regex UnitPattern { get; }
}
