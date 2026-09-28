using System.ComponentModel;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace Panel.Cli;

/// <summary>
/// Операции над машиной Windows для установки и управления: службы, команды панели,
/// браузер. Аналог <see cref="LinuxHost"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsHost
{
	private static readonly TimeSpan poll = TimeSpan.FromSeconds(1);

	public WindowsHost(WindowsPaths paths, TextWriter output)
	{
		Paths = paths;
		Output = output;
	}

	public WindowsPaths Paths { get; }

	public TextWriter Output { get; }

	public void Step(string title) => Output.WriteLine($"\n==> {title}");

	public void Warn(string message) => Console.Error.WriteLine($"Warning: {message}");

	/// <summary>Состояние службы; <see langword="null"/>, если она не установлена.</summary>
	public static ServiceControllerStatus? Status(string name)
	{
		try
		{
			using var service = new ServiceController(name);
			return service.Status;
		}
		catch (InvalidOperationException)
		{
			return null;
		}
	}

	public static bool IsRunning(string name) => Status(name) == ServiceControllerStatus.Running;

	/// <summary>
	/// Запустить службу и дождаться состояния «работает». Панель сообщает его, когда Kestrel уже
	/// слушает, поэтому к этому моменту страница открывается.
	/// </summary>
	public async Task<bool> StartAsync(string name, int seconds)
	{
		using var service = new ServiceController(name);
		try
		{
			if (service.Status == ServiceControllerStatus.StopPending)
			{
				await WaitAsync(service, ServiceControllerStatus.Stopped, seconds).ConfigureAwait(false);
			}

			if (service.Status is ServiceControllerStatus.Running)
			{
				Output.WriteLine($"{name} is already running.");
				return true;
			}

			if (service.Status is ServiceControllerStatus.Stopped)
			{
				service.Start();
			}
		}
		catch (InvalidOperationException exception)
		{
			await Console.Error.WriteLineAsync($"{name} did not start: {Reason(exception)}").ConfigureAwait(false);
			return false;
		}

		var elapsed = 0;
		while (elapsed < seconds)
		{
			service.Refresh();
			if (service.Status == ServiceControllerStatus.Running)
			{
				Output.WriteLine($"{name} started.");
				return true;
			}

			if (service.Status == ServiceControllerStatus.Stopped)
			{
				await Console.Error.WriteLineAsync($"{name} stopped immediately after starting. Check Windows Event Viewer → Windows Logs → Application.").ConfigureAwait(false);
				return false;
			}

			if (elapsed > 0 && elapsed % 10 == 0)
			{
				Output.WriteLine($"Waiting for {name} to start: {service.Status}, {elapsed} s elapsed...");
			}

			await Task.Delay(poll).ConfigureAwait(false);
			elapsed++;
		}

		await Console.Error.WriteLineAsync($"{name} did not start within {seconds} s. Check: vs-panel status").ConfigureAwait(false);
		return false;
	}

	/// <summary>
	/// Остановить службу. <c>ServiceController.Stop</c> сначала останавливает зависимые службы,
	/// поэтому вместе с демоном останавливается и панель. Игровые серверы продолжают работать (§65).
	/// </summary>
	public async Task<bool> StopAsync(string name, int seconds)
	{
		using var service = new ServiceController(name);
		try
		{
			if (service.Status is ServiceControllerStatus.Stopped)
			{
				return true;
			}

			if (service.Status is not ServiceControllerStatus.StopPending)
			{
				service.Stop();
			}
		}
		catch (InvalidOperationException exception)
		{
			await Console.Error.WriteLineAsync($"{name} did not stop: {Reason(exception)}").ConfigureAwait(false);
			return false;
		}

		if (await WaitAsync(service, ServiceControllerStatus.Stopped, seconds).ConfigureAwait(false))
		{
			Output.WriteLine($"{name} stopped.");
			return true;
		}

		await Console.Error.WriteLineAsync($"{name} did not stop within {seconds} s: the panel may be finishing a long operation.").ConfigureAwait(false);
		return false;
	}

	/// <summary>
	/// <c>vs-panel-web manage …</c> от имени пользователя, запустившего установщик, то есть той же
	/// учётной записи, что у служб. Окружение не передаётся: на Windows панель сама читает
	/// <c>vs-panel.env</c>.
	/// </summary>
	public async Task<ShellResult> ManageAsync(IEnumerable<string> command)
	{
		// Вывод показывается сразу: создание сервера занимает минуты и сообщает о каждом шаге.
		var result = await Shell.RunAsync(Paths.CurrentBinary("vs-panel-web"), ["manage", .. command], TimeSpan.FromMinutes(30), echo: Output).ConfigureAwait(false);

		return result;
	}

	/// <summary><c>manage</c> с терминалом пользователя, в котором вводится пароль.</summary>
	public Task<int> ManageAttachedAsync(IEnumerable<string> command) =>
		Shell.RunAttachedAsync(Paths.CurrentBinary("vs-panel-web"), ["manage", .. command]);

	/// <summary>
	/// Открыть страницу в браузере через оболочку рабочего стола (<see cref="DesktopShell"/>):
	/// установщик работает с повышенными правами, и браузер, запущенный им напрямую, получил бы
	/// их же.
	/// </summary>
	public async Task OpenAsync(string url)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(url);
		if (!url.StartsWith("http://", StringComparison.Ordinal) && !url.StartsWith("https://", StringComparison.Ordinal))
		{
			return;
		}

		Output.WriteLine(await DesktopShell.OpenAsync(url).ConfigureAwait(false)
			? "Page opened in the browser."
			: "Could not open the browser; open the link above manually.");
	}

	public Dictionary<string, string> PanelEnvironment() => EnvironmentFile.Load(Paths.PanelEnv);

	public string StateDirectory() => EnvironmentFile.Value(PanelEnvironment(), "PANEL_STATE_DIR", Paths.StateDirectory);

	public int? RegisteredServers() => PanelState.RegisteredServers(Paths.Registry);

	public bool SetupCompleted() => PanelState.SetupCompleted(StateDirectory());

	private static async Task<bool> WaitAsync(ServiceController service, ServiceControllerStatus status, int seconds)
	{
		for (var elapsed = 0; elapsed < seconds; elapsed++)
		{
			service.Refresh();
			if (service.Status == status)
			{
				return true;
			}

			await Task.Delay(poll).ConfigureAwait(false);
		}

		service.Refresh();
		return service.Status == status;
	}

	/// <summary>Причина отказа диспетчера служб из вложенной ошибки Win32: она подробнее.</summary>
	private static string Reason(InvalidOperationException exception) =>
		exception.InnerException is Win32Exception win32 ? win32.Message : exception.Message;
}
