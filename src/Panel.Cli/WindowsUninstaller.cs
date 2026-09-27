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
	public const string Confirmation = "удалить";

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
					throw new InstallFailed($"Неизвестный параметр: {argument}");
			}
		}

		var roots = ServersRoots();
		if (purge && !confirmed)
		{
			Output.WriteLine($"Будут удалены программа {Paths.ProgramRoot}, состояние {this.host.StateDirectory()} (учётные записи,");
			Output.WriteLine($"аудит, история операций) и настройки {Paths.PanelEnv}, {Paths.PanelConfig}.");
			Output.WriteLine($"Игровые серверы в {roots}, их миры и копии останутся.");
			Output.Write($"Для подтверждения введите: {Confirmation}\n> ");
			if (Console.ReadLine()?.Trim() != Confirmation)
			{
				throw new InstallFailed("Отменено.");
			}
		}

		foreach (var service in new[] { WindowsPaths.PanelService, WindowsPaths.AgentService })
		{
			if (WindowsHost.Status(service) is not null)
			{
				await this.host.StopAsync(service, 120).ConfigureAwait(false);
				ServiceManager.Delete(service);
				Output.WriteLine($"Служба {service} удалена.");
			}
		}

		// Порт панели больше никто не слушает; повторная установка откроет его снова.
		await PanelAccess.RemoveAsync(PanelListener.FromEnvironment(this.host.PanelEnvironment()), windows: true).ConfigureAwait(false);

		foreach (var folder in WindowsInstaller.ShortcutFolders())
		{
			File.Delete(Path.Combine(folder, WindowsInstaller.ShortcutName));
		}

		var servers = this.host.RegisteredServers() ?? 1;
		if (servers > 0)
		{
			this.host.Warn($"в реестре серверов: {servers}. Запущенные продолжат работу до перезагрузки, но запускать их больше некому.");
		}

		if (!purge)
		{
			// Сама команда остаётся: через неё выполняется полное удаление.
			Output.WriteLine("Службы панели и демона удалены.");
			Output.WriteLine($"Сохранены: программа {Paths.ProgramRoot}, данные и настройки {Paths.DataRoot}.");
			Output.WriteLine("Повторная установка вернёт панель с прежними учётными записями.");
			Output.WriteLine("Полное удаление: vs-panel uninstall --purge");
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

		File.Delete(Paths.PanelEnv);
		File.Delete(Paths.PanelConfig);
		if (servers == 0)
		{
			File.Delete(Paths.Registry);
		}

		// Запущенный vs-panel.exe лежит в каталоге программы, и Windows не даст его удалить.
		// Остальное удаляется сейчас, каталог — при следующей возможности.
		if (!TryDelete(Paths.ProgramRoot))
		{
			Output.WriteLine($"Каталог {Paths.ProgramRoot} занят этой командой — удалите его вручную после выхода.");
		}

		Output.WriteLine("Панель удалена полностью.");
		if (servers != 0)
		{
			Output.WriteLine($"Реестр {Paths.Registry} и серверы в {roots} сохранены.");
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
