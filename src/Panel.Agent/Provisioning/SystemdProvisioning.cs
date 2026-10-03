using System.Globalization;
using System.Runtime.Versioning;
using Panel.Content.Domain;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Provisioning;

/// <summary>
/// Создание сервера на Linux: игровой пользователь, <c>chown</c>, <c>--genconfig</c> через
/// <c>setpriv</c> и юнит systemd из шаблона.
/// </summary>
/// <remarks>
/// Игра никогда не запускается от root, даже для <c>--genconfig</c>: дерево
/// отдаётся игровому пользователю до запуска, а запуск идёт через <c>setpriv</c> с очисткой
/// дополнительных групп. <c>HOME</c> и <c>DOTNET_CLI_HOME</c> указывают на каталог данных
/// сервера, потому что домашнего каталога у учётной записи нет.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class SystemdProvisioning : IProvisioningPlatform
{
	private const string panelUnit = "vs-panel.service";

	private const string agentUnit = "vs-panel-agent.service";

	private const string stat = "/usr/bin/stat";

	private const string ufw = "/usr/sbin/ufw";

	private static readonly UnixFileMode mode644 =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

	private static readonly UnixFileMode mode755 =
		mode644 | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

	private readonly string runDirectory;

	/// <param name="runDirectory">Каталог выполнения: под ним systemd создаёт каталог FIFO сервера.</param>
	public SystemdProvisioning(string runDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);
		this.runDirectory = runDirectory;
	}

	public PlatformLayout Layout => PlatformLayout.Linux;

	/// <summary>Каталог юнитов systemd. Свойство, чтобы тесты могли подставить временный каталог.</summary>
	public string UnitDirectory { get; init; } = "/etc/systemd/system";

	public Task<RuntimeProbePayload> ProbeRuntimeAsync(CancellationToken ct) => HostTools.ProbeRuntimeAsync(ct);

	public async Task<Result<string>> PrepareAsync(InstanceLayout paths, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(paths);

		var dotnet = HostTools.FindDotnet();
		if (dotnet is null)
		{
			return Failure.Unavailable("dotnet not found; install the .NET runtime the game requires");
		}

		if (HostTools.FindSetpriv() is null)
		{
			return Failure.Unavailable("setpriv not found; install util-linux: the game is never started as root");
		}

		var user = await HostTools.EnsureGameUserAsync(GameUnit.GameUser, paths.InstancesRoot, ct).ConfigureAwait(false);
		return user.IsSuccess ? dotnet : Result.Fail<string>(user.Failure!);
	}

	/// <summary>
	/// Предки принадлежат root и закрыты на запись для остальных, сам каталог — root:root 0755.
	/// Промежуточные каталоги демон не создаёт.
	/// </summary>
	public async Task<Result<Done>> SecureServersRootAsync(ServersRoot root, bool owned, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(root);

		if (!Directory.Exists(root.Parent))
		{
			return Failure.Invalid($"Каталога {root.Parent} нет: каталог серверов создаётся только внутри существующего");
		}

		for (var ancestor = new DirectoryInfo(root.Parent); ancestor is not null; ancestor = ancestor.Parent)
		{
			var facts = await StatAsync(ancestor.FullName, ct).ConfigureAwait(false);
			if (!facts.IsSuccess)
			{
				return facts.Failure!;
			}

			if (LinuxServersRoot.Weakness(ancestor.FullName, facts.Value.Uid, facts.Value.Mode) is { } weakness)
			{
				return Failure.Invalid(weakness);
			}
		}

		if (Directory.Exists(root.Path) && !owned)
		{
			var facts = await StatAsync(root.Path, ct).ConfigureAwait(false);
			if (!facts.IsSuccess)
			{
				return facts.Failure!;
			}

			if (facts.Value.Uid != 0 && Directory.EnumerateFileSystemEntries(root.Path).Any())
			{
				return Failure.Conflict($"Каталог {root.Path} не пуст и принадлежит не root: выберите пустой или новый каталог");
			}
		}

		Directory.CreateDirectory(root.Path);
		var chowned = await HostTools.RunAsync(HostTools.Chown, ["root:root", "--", root.Path], TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
		if (chowned.ExitCode != 0)
		{
			return Failure.Unavailable(chowned.Tail($"Не удалось сменить владельца {root.Path}"));
		}

		File.SetUnixFileMode(root.Path, mode755);
		return Done.Ok;
	}

	private static async Task<Result<(int Uid, int Mode)>> StatAsync(string path, CancellationToken ct)
	{
		var result = await HostTools.RunAsync(stat, ["-L", "-c", "%u %a", "--", path], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
		return result.ExitCode == 0 && LinuxServersRoot.Parse(result.StandardOutput) is { } facts
			? facts
			: Failure.Unavailable(result.Tail($"Не удалось прочитать владельца и права {path}"));
	}

	public Task<Result<Done>> GiveToGameAsync(string root, CancellationToken ct) =>
		HostTools.ChownTreeAsync(root, GameUnit.GameUser, ct);

	public Task<HostCommandResult> GenerateConfigAsync(string dotnet, InstanceLayout paths, TimeSpan timeout, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(paths);

		var environment = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			["PATH"] = "/usr/local/bin:/usr/bin:/bin",
			["HOME"] = paths.DataDirectory,
			["DOTNET_CLI_HOME"] = paths.DataDirectory,
			["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
			["DOTNET_NOLOGO"] = "1",
		};

		return HostTools.RunAsync(
			HostTools.Setpriv,
			[
				$"--reuid={GameUnit.GameUser}", $"--regid={GameUnit.GameUser}", "--clear-groups", "--",
				dotnet, Path.Combine(paths.ProgramDirectory, ServerPackage.ServerDll), "--dataPath", paths.DataDirectory, "--genconfig",
			],
			timeout,
			ct,
			environment,
			paths.ProgramDirectory);
	}

	/// <summary>
	/// Юнит из шаблона, <c>daemon-reload</c>, <c>enable</c>.
	/// </summary>
	public async Task<Result<ServiceInstalledPayload>> InstallServiceAsync(
		InstanceLayout paths, RegisteredServer server, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(paths);

		var dotnet = HostTools.FindDotnet();
		if (dotnet is null)
		{
			return Failure.Unavailable("dotnet not found; install the .NET runtime the game requires");
		}

		if (!File.Exists(HostTools.ConsoleWrapper))
		{
			return Failure.Unavailable("Console wrapper is not installed");
		}

		if (!File.Exists(Path.Combine(paths.ProgramDirectory, ServerPackage.ServerDll)))
		{
			return Failure.Unavailable("Server program directory has no VintagestoryServer.dll");
		}

		var unit = GameUnit.Create(paths, dotnet, HostTools.ConsoleWrapper);
		if (!unit.IsSuccess)
		{
			return unit.Failure!;
		}

		var unitFile = Path.Combine(UnitDirectory, paths.Unit);
		var stored = await ProvisionBackend.WriteAtomicallyAsync(unitFile, unit.Value.Text, mode644, ct).ConfigureAwait(false);
		if (!stored.IsSuccess)
		{
			return stored.Failure!;
		}

		var reloaded = await HostTools.SystemctlAsync(["daemon-reload"], TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
		if (!reloaded.IsSuccess)
		{
			return reloaded.Failure!;
		}

		var enabled = await HostTools.SystemctlAsync(["enable", paths.Unit], TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
		if (!enabled.IsSuccess)
		{
			return enabled.Failure!;
		}

		return new ServiceInstalledPayload { Id = paths.Id.Value, Unit = paths.Unit, Path = unitFile };
	}

	/// <summary>
	/// Остановить, снять с автозапуска, удалить файл юнита. Если юнит не остановился, это отказ:
	/// стирать дерево под работающим процессом нельзя.
	/// </summary>
	public async Task<Result<Done>> RemoveServiceAsync(InstanceLayout paths, RegisteredServer server, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(paths);

		var unit = paths.Unit;
		await HostTools.RunAsync(HostTools.Systemctl, ["stop", unit], TimeSpan.FromSeconds(300), ct).ConfigureAwait(false);
		var active = await HostTools.RunAsync(HostTools.Systemctl, ["is-active", "--quiet", unit], TimeSpan.FromSeconds(30), ct)
			.ConfigureAwait(false);
		if (active.ExitCode == 0)
		{
			return Failure.Conflict($"Cannot stop {unit}; stop it manually before deleting the server");
		}

		await HostTools.RunAsync(HostTools.Systemctl, ["disable", unit], TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
		var unitFile = Path.Combine(UnitDirectory, unit);
		if (File.Exists(unitFile))
		{
			File.Delete(unitFile);
			await HostTools.RunAsync(HostTools.Systemctl, ["daemon-reload"], TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
		}

		ProvisionBackend.TryDelete(Path.Combine(runDirectory, paths.RuntimeDirectory));
		return Done.Ok;
	}

	/// <summary>
	/// Журнал юнитов панели и демона, общий для всех серверов, строками <c>short-iso</c>, как их
	/// печатает journalctl: обе службы одним потоком по времени, отказ демона рядом с запросом панели.
	/// </summary>
	public async Task<Result<PanelLogsPayload>> PanelLogsAsync(int count, CancellationToken ct)
	{
		var result = await HostTools.RunAsync(
				HostTools.Journalctl,
				["--unit", panelUnit, "--unit", agentUnit, "--no-pager", "--output=short-iso", "--lines", count.ToString(CultureInfo.InvariantCulture)],
				TimeSpan.FromSeconds(30),
				ct)
			.ConfigureAwait(false);
		if (result.ExitCode != 0)
		{
			return Failure.Unavailable(result.Tail("Cannot read panel journal"));
		}

		return new PanelLogsPayload { Lines = SplitLines(result.StandardOutput), Unit = $"{panelUnit}, {agentUnit}" };
	}

	/// <summary>
	/// Перезапуск юнита панели. С <c>--no-block</c> команда возвращается раньше, чем systemd
	/// остановит панель, и ответ успевает дойти до браузера.
	/// </summary>
	public async Task<Result<PanelRestartPayload>> RestartPanelAsync(CancellationToken ct)
	{
		var restarted = await HostTools.SystemctlAsync(["restart", "--no-block", panelUnit], TimeSpan.FromSeconds(30), ct)
			.ConfigureAwait(false);
		return restarted.IsSuccess ? new PanelRestartPayload { Unit = panelUnit } : restarted.Failure!;
	}

	/// <summary>
	/// Правила ufw для порта панели, как у установщика (<c>PanelAccess</c>). Без ufw или при
	/// неактивном ufw ничего не делается: правил, которые никто не применяет, не заводим.
	/// </summary>
	public async Task<Phrase?> SyncPanelFirewallAsync(PanelListener previous, PanelListener current, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(previous);
		ArgumentNullException.ThrowIfNull(current);
		if (previous == current || !File.Exists(ufw))
		{
			return null;
		}

		var status = await HostTools.RunAsync(ufw, ["status"], TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
		if (status.ExitCode != 0 || !status.StandardOutput.Contains("Status: active", StringComparison.Ordinal))
		{
			return null;
		}

		foreach (var rule in previous.UfwDelete)
		{
			await HostTools.RunAsync(ufw, rule, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
		}

		foreach (var rule in current.UfwAllow)
		{
			var added = await HostTools.RunAsync(ufw, rule, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
			if (added.ExitCode != 0)
			{
				return Phrase.Format($"Порт панели {current.Port.ToString(CultureInfo.InvariantCulture)} в ufw не открыт: {added.Tail("ufw отказал")}");
			}
		}

		return null;
	}

	/// <summary><c>str.splitlines()</c> для вывода journalctl: без пустого хвоста и без <c>\r</c>.</summary>
	private static List<string> SplitLines(string text)
	{
		var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
		if (lines.Count > 0 && lines[^1].Length == 0)
		{
			lines.RemoveAt(lines.Count - 1);
		}

		return lines;
	}
}
