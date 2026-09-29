using System.Runtime.Versioning;
using Panel.Fleet.Domain;
using Panel.Fleet.Infrastructure;
using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Cli;

/// <summary>
/// <c>vs-panel uninstall [--purge [--servers]] [--yes]</c>.
/// </summary>
/// <remarks>
/// <para>
/// Игровые серверы по умолчанию не затрагиваются: их юниты, миры и копии остаются, и они
/// работают как обычные службы systemd. Поэтому обёртка консоли не удаляется, пока в реестре
/// есть серверы: юнит сервера с транспортом fifo без неё не запустится.
/// </para>
/// <para>
/// При <c>--purge</c> команда спрашивает отдельной фразой, стереть ли и серверы, созданные
/// панелью (<see cref="ServerPurge"/>); <c>--servers</c> отвечает «да» заранее, <c>--yes</c> без
/// него — «нет». Иначе после полного удаления и новой установки панель снова находила бы
/// прежний реестр, а единственный сервер из панели не удалить.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class Uninstaller
{
	/// <summary>Фраза подтверждения полного удаления.</summary>
	public const string Confirmation = "delete";

	/// <summary>Фраза подтверждения удаления игровых серверов вместе с панелью.</summary>
	public const string ServersConfirmation = "delete servers";

	/// <summary>Остановка игрового сервера: он сохраняет мир, это дольше остановки панели.</summary>
	private static readonly TimeSpan serverStop = TimeSpan.FromMinutes(6);

	private readonly LinuxHost host;

	public Uninstaller(LinuxHost host) => this.host = host;

	private InstallPaths Paths => this.host.Paths;

	public async Task<int> RunAsync(IReadOnlyList<string> arguments)
	{
		var purge = false;
		var confirmed = false;
		var deleteServers = false;
		foreach (var argument in arguments)
		{
			switch (argument)
			{
				case "--purge":
					purge = true;
					break;
				case "--servers":
					deleteServers = true;
					break;
				case "--yes" or "-y":
					confirmed = true;
					break;
				default:
					throw new InstallFailed($"Unknown option: {argument}");
			}
		}

		if (deleteServers && !purge)
		{
			throw new InstallFailed("--servers is used only with --purge");
		}

		// Реестр и vs-panel.json читаются до удаления: после него серверы не найти.
		var (configuration, created) = purge ? await PlanAsync().ConfigureAwait(false) : (null, []);
		if (purge && !confirmed)
		{
			this.host.Output.WriteLine("The program /opt/vs-panel, state /var/lib/vs-panel (accounts,");
			this.host.Output.WriteLine("audit log, operation history), settings /etc/vs-panel.env and /etc/vs-panel.json,");
			this.host.Output.WriteLine("and the vs-panel user will be removed.");
			this.host.Output.Write($"To confirm, type: {Confirmation}\n> ");
			if (Console.ReadLine()?.Trim() != Confirmation)
			{
				throw new InstallFailed("Cancelled.");
			}

			if (created.Count > 0 && !deleteServers)
			{
				this.host.Output.WriteLine();
				this.host.Output.WriteLine("Game servers created by the panel:");
				foreach (var server in created)
				{
					this.host.Output.WriteLine($"  {server.Server.Id.Value}  {server.Layout.Root}");
				}

				this.host.Output.WriteLine("Delete them too, with their worlds, mods and backups? This cannot be undone.");
				this.host.Output.Write($"To delete the servers, type: {ServersConfirmation}; press Enter to keep them\n> ");
				deleteServers = Console.ReadLine()?.Trim() == ServersConfirmation;
			}
		}

		await this.host.SystemctlAsync("disable", "--now", InstallPaths.PanelUnitName).ConfigureAwait(false);
		await this.host.SystemctlAsync("disable", "--now", InstallPaths.AgentUnitName).ConfigureAwait(false);
		foreach (var file in new[] { Paths.PanelUnit, Paths.AgentUnit, Paths.LegacySudoers, Paths.LegacyHelper, Paths.LegacyClaim })
		{
			Files.Delete(file);
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

		// Серверы — до настроек панели: реестр правится через vs-panel.json.
		var serversRemoved = deleteServers && configuration is not null
			? await RemoveServersAsync(configuration, created).ConfigureAwait(false)
			: 0;

		var servers = this.host.RegisteredServers() ?? 1;
		var state = this.host.StateDirectory();
		foreach (var directory in new[] { Paths.ProgramRoot, state, Paths.StateDirectory, Paths.SharedData })
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
			if (Directory.Exists(Paths.RegistryDirectory))
			{
				Directory.Delete(Paths.RegistryDirectory, recursive: true);
			}

			Files.Delete(Paths.ConsoleWrapper);
		}

		if (await LinuxHost.UidAsync(InstallPaths.PanelUser).ConfigureAwait(false) is not null)
		{
			await Shell.RunAsync("userdel", [InstallPaths.PanelUser]).ConfigureAwait(false);
		}

		// Учётная запись игры общая для всех серверов: удаляется, только если серверов не осталось.
		if (servers == 0 && serversRemoved > 0 && await LinuxHost.UidAsync(GameUnit.GameUser).ConfigureAwait(false) is not null)
		{
			await Shell.RunAsync("userdel", [GameUnit.GameUser]).ConfigureAwait(false);
		}

		Files.Delete(Paths.CommandLink);
		this.host.Output.WriteLine("Panel removed completely.");
		if (servers != 0)
		{
			this.host.Output.WriteLine($"Registry {Paths.Registry} and console wrapper were preserved: the registry lists game servers ({servers}),");
			this.host.Output.WriteLine("which continue running as regular systemd services.");
		}

		return 0;
	}

	/// <summary>Настройки панели и серверы реестра, которые она создала; пусто, если прочитать их нельзя.</summary>
	private async Task<(PanelConfiguration? Configuration, IReadOnlyList<PurgedServer> Created)> PlanAsync()
	{
		PanelConfiguration configuration;
		try
		{
			var parsed = PanelConfiguration.Parse(await File.ReadAllTextAsync(Paths.PanelConfig).ConfigureAwait(false));
			if (!parsed.IsSuccess)
			{
				return (null, []);
			}

			configuration = parsed.Value;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return (null, []);
		}

		var snapshot = await new FileRegistryStore(Paths.PanelConfig).LoadAsync(CancellationToken.None).ConfigureAwait(false);
		if (snapshot is not RegistrySnapshot.Available available)
		{
			return (configuration, []);
		}

		return (configuration, ServerPurge.Plan(configuration, available.Servers).Created);
	}

	/// <summary>
	/// Остановить и снять юниты серверов, стереть их деревья, убрать их из реестра. Сервер, который
	/// не остановился или стёрт не полностью, остаётся в реестре. Возвращает число удалённых.
	/// </summary>
	private async Task<int> RemoveServersAsync(PanelConfiguration configuration, IReadOnlyList<PurgedServer> created)
	{
		var removed = new List<InstanceId>();
		foreach (var (server, layout) in created)
		{
			var id = server.Id.Value;
			this.host.Output.WriteLine($"Stopping server {id}...");
			await Shell.RunAsync("systemctl", ["stop", layout.Unit], serverStop).ConfigureAwait(false);
			if (await this.host.IsActiveAsync(layout.Unit).ConfigureAwait(false))
			{
				this.host.Warn($"{layout.Unit} did not stop; server {id} was kept.");
				continue;
			}

			await this.host.SystemctlAsync("disable", layout.Unit).ConfigureAwait(false);
			Files.Delete(Path.Combine(Paths.UnitDirectory, layout.Unit));
			try
			{
				Directory.Delete(Path.Combine(configuration.RunDirectory, layout.RuntimeDirectory), recursive: true);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// Каталог выполнения снимает systemd при остановке; остаток исчезнет при перезагрузке.
			}

			try
			{
				Directory.Delete(layout.Root, recursive: true);
			}
			catch (DirectoryNotFoundException)
			{
				// Дерева уже нет.
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				this.host.Warn($"{layout.Root} could not be erased: {exception.Message}. Server {id} was kept in the registry.");
				continue;
			}

			removed.Add(server.Id);
			this.host.Output.WriteLine($"Server {id} removed: {layout.Root}");
		}

		if (removed.Count == 0)
		{
			return 0;
		}

		await this.host.SystemctlAsync("daemon-reload").ConfigureAwait(false);
		var roots = created
			.Where(server => removed.Contains(server.Server.Id))
			.Select(server => server.Layout.InstancesRoot)
			.Distinct(StringComparer.Ordinal);
		foreach (var root in roots)
		{
			// Каталог серверов создал демон; пустой он больше не нужен.
			if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
			{
				Directory.Delete(root);
			}
		}

		var edited = await new RegistryEditor(Paths.PanelConfig).EditAsync(document =>
		{
			foreach (var id in removed.Where(id => document.Registered.Contains(id)))
			{
				// Без последнего сервера умолчания нет: реестр удаляется целиком вместе с панелью.
				document.Default = document.Registered.Count > 1
					? RegistryRules.DefaultAfterRemoving(document.Registered, document.Default, id)
					: null;
				document.Instances.Remove(id.Value);
			}

			return Result.Ok(Done.Ok);
		}, CancellationToken.None).ConfigureAwait(false);
		if (!edited.IsSuccess)
		{
			this.host.Warn($"The registry was not updated: {edited.Failure!.Message}");
		}

		return removed.Count;
	}
}
