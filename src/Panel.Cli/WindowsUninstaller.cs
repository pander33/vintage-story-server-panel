using System.Runtime.Versioning;
using Panel.Provisioning.Domain;

namespace Panel.Cli;

/// <summary>
/// <c>vs-panel uninstall [--purge] [--yes]</c> на Windows, аналог <see cref="Uninstaller"/>.
/// </summary>
/// <remarks>
/// Игровые серверы не затрагиваются: миры, копии и программа сервера остаются. Работающий
/// сервер продолжит работать (Windows не завершает дочерние процессы вместе с демоном), но
/// после перезагрузки его никто не запустит.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsUninstaller
{
	/// <summary>Фраза подтверждения полного удаления, как на Linux.</summary>
	public const string Confirmation = "delete";

	private readonly WindowsHost host;

	public WindowsUninstaller(WindowsHost host) => this.host = host;

	private WindowsPaths Paths => this.host.Paths;

	private TextWriter Output => this.host.Output;

	public async Task<int> RunAsync(IReadOnlyList<string> arguments)
	{
		var purge = false;
		var confirmed = false;
		foreach (var argument in arguments)
		{
			switch (argument)
			{
				case "--purge":
					purge = true;
					break;
				case "--yes" or "-y":
					confirmed = true;
					break;
				default:
					throw new InstallFailed($"Unknown option: {argument}");
			}
		}

		var roots = ServersRoots();
		if (purge && !confirmed)
		{
			Output.WriteLine($"The program {Paths.ProgramRoot}, state {this.host.StateDirectory()} (accounts,");
			Output.WriteLine($"audit log, operation history) and settings {Paths.PanelEnv}, {Paths.PanelConfig} will be removed.");
			Output.WriteLine($"Game servers in {roots}, their worlds and backups will remain.");
			Output.Write($"To confirm, type: {Confirmation}\n> ");
			if (Console.ReadLine()?.Trim() != Confirmation)
			{
				throw new InstallFailed("Cancelled.");
			}
		}

		foreach (var service in new[] { WindowsPaths.PanelService, WindowsPaths.AgentService })
		{
			if (WindowsHost.Status(service) is not null)
			{
				await this.host.StopAsync(service, 120).ConfigureAwait(false);
				ServiceManager.Delete(service);
				Output.WriteLine($"Service {service} removed.");
			}
		}

		// Порт панели больше никто не слушает; повторная установка откроет его снова.
		await PanelAccess.RemoveAsync(PanelListener.FromEnvironment(this.host.PanelEnvironment()), windows: true).ConfigureAwait(false);

		foreach (var folder in WindowsInstaller.ShortcutFolders())
		{
			Files.Delete(Path.Combine(folder, WindowsInstaller.ShortcutName));
		}

		var servers = this.host.RegisteredServers() ?? 1;
		if (servers > 0)
		{
			this.host.Warn($"Server registry contains {servers}. Running servers will continue until reboot, but nothing will start them afterward.");
		}

		if (!purge)
		{
			// Сама команда остаётся: через неё выполняется полное удаление.
			Output.WriteLine("Panel and agent services removed.");
			Output.WriteLine($"Preserved: program {Paths.ProgramRoot}, data and settings {Paths.DataRoot}.");
			Output.WriteLine("Reinstalling will restore the panel with its previous accounts.");
			Output.WriteLine("Full removal: vs-panel uninstall --purge");
			return 0;
		}

		RemoveFromPath();
		var state = this.host.StateDirectory();
		foreach (var directory in new[] { state, Paths.StateDirectory, Paths.RunDirectory, Paths.TelemetryDirectory })
		{
			if (Directory.Exists(directory))
			{
				Directory.Delete(directory, recursive: true);
			}
		}

		Files.Delete(Paths.PanelEnv);
		Files.Delete(Paths.PanelConfig);
		if (servers == 0)
		{
			Files.Delete(Paths.Registry);
		}

		// Запущенный vs-panel.exe лежит в каталоге программы, и Windows не даст его удалить.
		// Остальное удаляется сейчас, каталог — при следующей возможности.
		if (!TryDelete(Paths.ProgramRoot))
		{
			Output.WriteLine($"Directory {Paths.ProgramRoot} is in use by this command — remove it manually after exit.");
		}

		Output.WriteLine("Panel removed completely.");
		if (servers != 0)
		{
			Output.WriteLine($"Registry {Paths.Registry} and servers in {roots} were preserved.");
		}

		return 0;
	}

	/// <summary>Каталоги серверов, текущий и прежние, из <c>vs-panel.json</c>, пока он ещё есть.</summary>
	private string ServersRoots()
	{
		try
		{
			var parsed = Panel.Fleet.Infrastructure.PanelConfiguration.Parse(File.ReadAllText(Paths.PanelConfig));
			if (parsed.IsSuccess)
			{
				return string.Join(", ", new[] { parsed.Value.InstancesRoot }.Concat(parsed.Value.FormerInstancesRoots).Distinct(StringComparer.OrdinalIgnoreCase));
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
		}

		return Paths.ServersDirectory;
	}

	private void RemoveFromPath()
	{
		var path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? string.Empty;
		var entries = path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		var kept = entries.Where(entry => !string.Equals(entry.TrimEnd('\\'), Paths.Current, StringComparison.OrdinalIgnoreCase)).ToArray();
		if (kept.Length != entries.Length)
		{
			Environment.SetEnvironmentVariable("PATH", string.Join(';', kept), EnvironmentVariableTarget.Machine);
		}
	}

	private static bool TryDelete(string directory)
	{
		try
		{
			if (Directory.Exists(directory))
			{
				Directory.Delete(directory, recursive: true);
			}

			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}
}
