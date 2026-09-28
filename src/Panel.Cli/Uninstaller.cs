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
	public const string Confirmation = "delete";

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
					throw new InstallFailed($"Unknown option: {argument}");
			}
		}

		if (purge && !confirmed)
		{
			this.host.Output.WriteLine("The program /opt/vs-panel, state /var/lib/vs-panel (accounts,");
			this.host.Output.WriteLine("audit log, operation history), settings /etc/vs-panel.env and /etc/vs-panel.json,");
			this.host.Output.WriteLine("and the vs-panel user will be removed. Game servers, worlds and backups will remain.");
			this.host.Output.Write($"To confirm, type: {Confirmation}\n> ");
			if (Console.ReadLine()?.Trim() != Confirmation)
			{
				throw new InstallFailed("Cancelled.");
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
			this.host.Output.WriteLine("Panel and agent services removed.");
			this.host.Output.WriteLine("Preserved: program /opt/vs-panel, state /var/lib/vs-panel, settings /etc/vs-panel*.");
			this.host.Output.WriteLine("Reinstalling will restore the panel with its previous accounts.");
			this.host.Output.WriteLine("Full removal: sudo vs-panel uninstall --purge");
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
		this.host.Output.WriteLine("Panel removed completely.");
		if (servers != 0)
		{
			this.host.Output.WriteLine($"Registry {Paths.Registry} and console wrapper were preserved: the registry lists game servers ({servers}),");
			this.host.Output.WriteLine("which continue running as regular systemd services.");
		}

		return 0;
	}
}
