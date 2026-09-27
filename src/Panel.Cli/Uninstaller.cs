using System.Runtime.Versioning;
using Panel.Provisioning.Domain;

namespace Panel.Cli;

/// <summary>
/// <c>vs-panel uninstall [--purge] [--yes]</c>.
/// </summary>
/// <remarks>
/// Игровые серверы при удалении панели не затрагиваются: их юниты, миры и копии остаются,
/// и они работают как обычные службы systemd. Поэтому обёртка консоли не удаляется, пока в
/// реестре есть серверы: юнит сервера с транспортом fifo без неё не запустится.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class Uninstaller
{
	/// <summary>Фраза подтверждения полного удаления.</summary>
	public const string Confirmation = "удалить";

	private readonly LinuxHost host;

	public Uninstaller(LinuxHost host) => this.host = host;

	private InstallPaths Paths => this.host.Paths;

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

		if (purge && !confirmed)
		{
			this.host.Output.WriteLine("Будут удалены программа /opt/vs-panel, состояние /var/lib/vs-panel (учётные записи,");
			this.host.Output.WriteLine("аудит, история операций), настройки /etc/vs-panel.env и /etc/vs-panel.json и");
			this.host.Output.WriteLine("пользователь vs-panel. Игровые серверы, их миры и копии останутся.");
			this.host.Output.Write($"Для подтверждения введите: {Confirmation}\n> ");
			if (Console.ReadLine()?.Trim() != Confirmation)
			{
				throw new InstallFailed("Отменено.");
			}
		}

		await this.host.SystemctlAsync("disable", "--now", InstallPaths.PanelUnitName).ConfigureAwait(false);
		await this.host.SystemctlAsync("disable", "--now", InstallPaths.AgentUnitName).ConfigureAwait(false);
		foreach (var file in new[] { Paths.PanelUnit, Paths.AgentUnit, Paths.LegacySudoers, Paths.LegacyHelper, Paths.LegacyClaim })
		{
			File.Delete(file);
		}

		await this.host.SystemctlAsync("daemon-reload").ConfigureAwait(false);

		// Порт панели больше никто не слушает; повторная установка откроет его снова.
		await PanelAccess.RemoveAsync(PanelListener.FromEnvironment(EnvironmentFile.Load(Paths.PanelEnv)), windows: false).ConfigureAwait(false);

		if (!purge)
		{
			// Сама команда остаётся: через неё выполняется полное удаление.
			this.host.Output.WriteLine("Службы панели и демона удалены.");
			this.host.Output.WriteLine("Сохранены: программа /opt/vs-panel, состояние /var/lib/vs-panel, настройки /etc/vs-panel*.");
			this.host.Output.WriteLine("Повторная установка вернёт панель с прежними учётными записями.");
			this.host.Output.WriteLine("Полное удаление: sudo vs-panel uninstall --purge");
			return 0;
		}

		var servers = this.host.RegisteredServers() ?? 1;
		var state = this.host.StateDirectory();
		foreach (var directory in new[] { Paths.ProgramRoot, state, Paths.StateDirectory, Paths.SharedData })
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
			if (Directory.Exists(Paths.RegistryDirectory))
			{
				Directory.Delete(Paths.RegistryDirectory, recursive: true);
			}

			File.Delete(Paths.ConsoleWrapper);
		}

		if (await LinuxHost.UidAsync(InstallPaths.PanelUser).ConfigureAwait(false) is not null)
		{
			await Shell.RunAsync("userdel", [InstallPaths.PanelUser]).ConfigureAwait(false);
		}

		File.Delete(Paths.CommandLink);
		this.host.Output.WriteLine("Панель удалена полностью.");
		if (servers != 0)
		{
			this.host.Output.WriteLine($"Реестр {Paths.Registry} и обёртка консоли сохранены: в реестре описаны игровые серверы ({servers}),");
			this.host.Output.WriteLine("которые продолжают работать как обычные службы systemd.");
		}

		return 0;
	}
}
