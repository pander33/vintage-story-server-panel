using System.Globalization;
using System.Runtime.Versioning;
using Panel.Provisioning.Domain;

namespace Panel.Cli;

/// <summary>
/// <c>vs-panel install | update | repair | rollback</c>.
/// </summary>
/// <remarks>
/// <para>
/// §2 заметки «Установщик и поставка .NET-панели»: отказ копировать поверх идущей операции;
/// реестр пишется один раз, плоский конфиг версии 1 переносится в него сервером main; пароль,
/// ключ сессий и аудит сохраняются при обновлении; панель перезапускается, а не включается
/// через «enable --now»; первый сервер создаёт установщик, и ошибка при этом не прерывает
/// установку; среда .NET ставится для игры.
/// </para>
/// <para>
/// Каждая версия ставится в свой каталог, <c>current</c> меняется атомарно, откат — та же
/// замена ссылки. Форматы файлов состояния установщик не знает: для этого есть команды
/// <c>vs-panel-web manage</c> (<c>init</c>, <c>claim-token</c>, <c>provision</c>), которые
/// выполняются от имени учётной записи панели.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class Installer
{
	private const UnixFileMode executable =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
		| UnixFileMode.GroupRead | UnixFileMode.GroupExecute
		| UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

	private const UnixFileMode readable =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

	private const UnixFileMode @private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

	private readonly LinuxHost host;

	public Installer(LinuxHost host) => this.host = host;

	private InstallPaths Paths => this.host.Paths;

	private TextWriter Output => this.host.Output;

	public async Task<int> InstallAsync(InstallOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		if (!(await Shell.RunAsync("systemctl", ["--version"]).ConfigureAwait(false)).Succeeded)
		{
			throw new InstallFailed("systemd is required.");
		}

		var artifact = Artifact.Open(options.From);
		if (!artifact.IsSuccess)
		{
			throw new InstallFailed(artifact.Failure!.Message);
		}

		// Остановка панели не прерывает операцию обновления, поэтому копирование поверх занятой
		// панели ждало бы TimeoutStopSec.
		if (await this.host.IsActiveAsync(InstallPaths.PanelUnitName).ConfigureAwait(false)
			&& MaintenanceProbe.Describe(this.host.StateDirectory()) is { } busy)
		{
			throw new InstallFailed($"Installation did not start: the panel is running {busy}.\nWait for the operation to finish in the panel and try again.");
		}

		var dotnetReady = true;
		if (!options.Repair)
		{
			if (!options.SkipPackages)
			{
				this.host.Step("Installing system packages");
				await InstallPackagesAsync().ConfigureAwait(false);
			}

			this.host.Step($"Installing .NET {options.DotnetChannel} runtime for the game");
			dotnetReady = options.SkipDotnet
				? Report("Skipped by VS_PANEL_SKIP_DOTNET=1.", true)
				: await InstallDotnetAsync(options.DotnetChannel).ConfigureAwait(false);
		}

		this.host.Step(options.Repair ? $"Repairing version {artifact.Value.Version}" : $"Installing version {artifact.Value.Version}");
		var uid = await this.host.EnsurePanelUserAsync().ConfigureAwait(false);
		LinuxHost.CreateDirectory(Paths.ProgramRoot, executable);
		LinuxHost.CreateDirectory(Paths.Releases, executable);
		LinuxHost.CreateDirectory(this.host.StateDirectory(), @private);
		LinuxHost.CreateDirectory(Path.Combine(this.host.StateDirectory(), "staging"), @private);
		if (!options.Repair)
		{
			PlaceRelease(artifact.Value);
		}

		// До переключения: если новая версия не запустит демон, вернётся прежняя вместе с её юнитами.
		var before = options.Repair ? null : CaptureRelease(artifact.Value.Version);
		await ActivateAsync(artifact.Value.Version).ConfigureAwait(false);
		await PruneAsync(artifact.Value.Version).ConfigureAwait(false);
		try
		{
			await StartDaemonAsync(options, artifact.Value, uid).ConfigureAwait(false);
		}
		catch (Exception exception) when (before is not null && exception is InstallFailed or IOException or UnauthorizedAccessException)
		{
			throw new InstallFailed(await RevertAsync(before).ConfigureAwait(false)
				? $"{exception.Message}\nVersion {before.Version} was restored and is running. Details: journalctl -u {InstallPaths.AgentUnitName}"
				: $"{exception.Message}\nVersion {before.Version} was restored, but its daemon did not start either. Details: journalctl -u {InstallPaths.AgentUnitName}");
		}

		this.host.Step("Initializing panel state");
		if (await this.host.ManageAsync(["init"]).ConfigureAwait(false) != 0)
		{
			throw new InstallFailed("Panel state was not initialized");
		}

		var setupLink = string.Empty;
		var state = this.host.StateDirectory();
		if (NonEmpty(Path.Combine(state, "accounts.json")) || NonEmpty(Path.Combine(state, "admin.password")))
		{
			Output.WriteLine("The existing administrator account was preserved.");
		}
		else
		{
			// Пароль в терминале не спрашивается: администратора создаёт мастер установки, а право
			// на это подтверждается одноразовым токеном.
			setupLink = "claim";
		}

		var rootReady = await ServersRootStep.RunAsync(
				options,
				Paths.PanelConfig,
				firstInstall: !options.Repair && this.host.RegisteredServers() is 0,
				new ServersRootStep.Terminal(Output, Console.IsInputRedirected ? null : Console.In, this.host.Step, this.host.Warn),
				async command => await this.host.ManageAsync(command).ConfigureAwait(false) == 0)
			.ConfigureAwait(false);
		var created = options.Repair || !options.CreateServer || !rootReady ? 0 : await CreateFirstServerAsync(options, dotnetReady).ConfigureAwait(false);

		this.host.Step("Restarting the panel");
		await Require(this.host.SystemctlAsync("enable", InstallPaths.PanelUnitName), "Panel service could not be enabled").ConfigureAwait(false);

		// `enable --now` не перезапускает уже активный юнит, и после обновления работала бы старая
		// версия. --no-block нужен, чтобы установщик не зависал, пока старая панель завершает
		// долгую операцию.
		var oldPid = await this.host.UnitPropertyAsync(InstallPaths.PanelUnitName, "MainPID").ConfigureAwait(false);
		await Require(this.host.SystemctlAsync("restart", "--no-block", InstallPaths.PanelUnitName), "Panel restart request was not accepted").ConfigureAwait(false);
		var started = await this.host.WaitForStartAsync(InstallPaths.PanelUnitName, oldPid, options.WaitSeconds).ConfigureAwait(false);

		PrintSummary(created);
		if (setupLink.Length > 0)
		{
			Output.WriteLine();
			await this.host.ManageAsync(["claim-token"]).ConfigureAwait(false);
			Output.WriteLine("\nIf you lose the link, get a new one with: sudo vs-panel access");
		}

		return started ? 0 : 1;
	}

	/// <summary>
	/// Всё от переключения <c>current</c> до запущенного демона: команда, обёртка консоли, реестр,
	/// настройки, брандмауэр, юниты и мод телеметрии. Состояние панели здесь ещё не тронуто — его
	/// меняет <c>manage init</c> новой версии, поэтому до него прежнюю версию можно вернуть.
	/// </summary>
	private async Task StartDaemonAsync(InstallOptions options, Artifact artifact, int uid)
	{
		this.host.Step("vs-panel command, console wrapper and systemd units");
		Directory.CreateDirectory(Path.GetDirectoryName(Paths.CommandLink)!);
		DeleteRegularFile(Paths.CommandLink);
		await LinuxHost.ReplaceLinkAsync(Paths.CommandLink, Paths.CurrentBinary("vs-panel")).ConfigureAwait(false);
		Directory.CreateDirectory(Path.GetDirectoryName(Paths.ConsoleWrapper)!);
		LinuxHost.WriteFile(Paths.ConsoleWrapper, Templates.ConsoleWrapper, executable);
		RemoveLegacyPrivilege();

		SeedRegistry();
		LinuxHost.WriteFile(Paths.PanelConfig, Templates.KeepChosenPaths(Templates.PanelConfig, File.Exists(Paths.PanelConfig) ? File.ReadAllText(Paths.PanelConfig) : null), readable);
		var existed = File.Exists(Paths.PanelEnv);
		var previous = existed ? PanelListener.FromEnvironment(EnvironmentFile.Load(Paths.PanelEnv)) : null;
		if (!existed || options.PanelEnvironment.Count > 0)
		{
			var text = existed ? File.ReadAllText(Paths.PanelEnv) : Templates.PanelEnvironment;
			LinuxHost.WriteFile(Paths.PanelEnv, PanelAccess.Rewrite(text, options.PanelEnvironment), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
			await this.host.ChownAsync($"root:{InstallPaths.PanelUser}", Paths.PanelEnv).ConfigureAwait(false);
		}

		this.host.Step("Panel access and firewall");
		var firewall = await PanelAccess.ApplyUfwAsync(previous, PanelListener.FromEnvironment(EnvironmentFile.Load(Paths.PanelEnv))).ConfigureAwait(false);
		ReportFirewall(firewall);

		LinuxHost.WriteFile(Paths.AgentUnit, Templates.AgentUnit(uid), readable);
		LinuxHost.WriteFile(Paths.PanelUnit, Templates.PanelUnit, readable);
		if (!options.Repair)
		{
			InstallTelemetry(artifact, options.TelemetryModDirectory);
		}

		this.host.Step("Panel daemon");
		await this.host.ChownAsync($"{InstallPaths.PanelUser}:{InstallPaths.PanelUser}", this.host.StateDirectory(), recursive: true).ConfigureAwait(false);
		await Require(this.host.SystemctlAsync("daemon-reload"), "systemctl daemon-reload").ConfigureAwait(false);
		await Require(this.host.SystemctlAsync("enable", InstallPaths.AgentUnitName), "Daemon could not be enabled").ConfigureAwait(false);

		// Демон нужен раньше панели: через него создаётся первый сервер. Панель с Requires= на
		// демон systemd перезапустит вместе с ним.
		await Require(this.host.SystemctlAsync("restart", InstallPaths.AgentUnitName), "Daemon failed to start").ConfigureAwait(false);
	}

	/// <summary>Вернуть предыдущую версию: замена ссылки, затем перезапуск демона и панели.</summary>
	public async Task<int> RollbackAsync(int waitSeconds)
	{
		var current = CurrentVersion() ?? throw new InstallFailed("No active version found: the panel is not installed");
		var history = ReleaseHistory.Parse(ReadOrEmpty(Paths.ReleaseLog));
		var previous = history.Previous(current, InstalledVersions())
			?? throw new InstallFailed($"Cannot roll back: no installed version other than {current}");

		if (await this.host.IsActiveAsync(InstallPaths.PanelUnitName).ConfigureAwait(false)
			&& MaintenanceProbe.Describe(this.host.StateDirectory()) is { } busy)
		{
			throw new InstallFailed($"Rollback did not start: the panel is running {busy}.");
		}

		this.host.Step($"Rolling back {current} → {previous}");
		await ActivateAsync(previous).ConfigureAwait(false);
		var oldPid = await this.host.UnitPropertyAsync(InstallPaths.PanelUnitName, "MainPID").ConfigureAwait(false);
		await Require(this.host.SystemctlAsync("restart", InstallPaths.AgentUnitName), "Daemon failed to start").ConfigureAwait(false);
		await Require(this.host.SystemctlAsync("restart", "--no-block", InstallPaths.PanelUnitName), "Panel restart request was not accepted").ConfigureAwait(false);
		return await this.host.WaitForStartAsync(InstallPaths.PanelUnitName, oldPid, waitSeconds).ConfigureAwait(false) ? 0 : 1;
	}

	/// <summary>Версия, на которую указывает <c>current</c>.</summary>
	public string? CurrentVersion() =>
		LinuxHost.LinkTarget(Paths.Current) is { } target ? Path.GetFileName(target.TrimEnd('/')) : null;

	public IReadOnlyCollection<string> InstalledVersions() =>
		Directory.Exists(Paths.Releases)
			? [.. Directory.GetDirectories(Paths.Releases).Select(Path.GetFileName).OfType<string>().Where(ReleaseHistory.IsVersion)]
			: [];

	/// <summary>
	/// Копирование сборки в <c>releases/&lt;версия&gt;</c>: во временный каталог, затем
	/// переименование. Повторная установка той же версии заменяет каталог целиком.
	/// </summary>
	private void PlaceRelease(Artifact artifact)
	{
		var target = Paths.Release(artifact.Version);
		if (Path.GetFullPath(artifact.Directory).TrimEnd('/') == Path.GetFullPath(target).TrimEnd('/'))
		{
			return;
		}

		var staging = target + ".new";
		if (Directory.Exists(staging))
		{
			Directory.Delete(staging, recursive: true);
		}

		LinuxHost.CreateDirectory(staging, executable);
		foreach (var binary in Artifact.Binaries)
		{
			LinuxHost.CopyFile(artifact.Source(binary), Path.Combine(staging, binary.Installed), executable);
		}

		LinuxHost.WriteFile(Path.Combine(staging, Artifact.VersionFile), artifact.Version + "\n", readable);
		if (artifact.Rid.Length > 0)
		{
			LinuxHost.WriteFile(Path.Combine(staging, Artifact.RidFile), artifact.Rid + "\n", readable);
		}

		if (Directory.Exists(target))
		{
			// Переустановка активной версии: работающие процессы держат открытыми файлы прежнего
			// каталога, и его удаление им не мешает.
			var retired = target + ".old";
			if (Directory.Exists(retired))
			{
				Directory.Delete(retired, recursive: true);
			}

			Directory.Move(target, retired);
			Directory.Move(staging, target);
			Directory.Delete(retired, recursive: true);
		}
		else
		{
			Directory.Move(staging, target);
		}

		Output.WriteLine($"Version {artifact.Version} copied to {target}");
	}

	private async Task ActivateAsync(string version)
	{
		await LinuxHost.ReplaceLinkAsync(Paths.Current, Paths.Release(version)).ConfigureAwait(false);
		var history = ReleaseHistory.Parse(ReadOrEmpty(Paths.ReleaseLog)).Activate(version);
		LinuxHost.WriteFile(Paths.ReleaseLog, history.Serialize(), readable);
		Output.WriteLine($"Active version: {version}");
	}

	/// <summary>
	/// Что вернуть, если новая версия не запустит демон: версия, журнал версий и юниты до установки.
	/// Юниты — потому что шаблоны меняются вместе с версией (например, <c>--socket-group</c>), и
	/// прежний демон с новым юнитом не стартует. <see langword="null"/> — возвращать нечего: панель
	/// ставится впервые или переустанавливается та же версия, чей каталог уже заменён.
	/// </summary>
	private ReleaseSnapshot? CaptureRelease(string installing) =>
		CurrentVersion() is { } version && version != installing && Directory.Exists(Paths.Release(version))
			? new ReleaseSnapshot(version, ReadOrNull(Paths.ReleaseLog), ReadOrNull(Paths.AgentUnit), ReadOrNull(Paths.PanelUnit))
			: null;

	/// <summary>
	/// Вернуть версию до установки. Настройки, реестр и брандмауэр остаются новыми: их читает и
	/// прежняя версия. Возвращает, запустился ли её демон.
	/// </summary>
	private async Task<bool> RevertAsync(ReleaseSnapshot before)
	{
		this.host.Step($"Restoring version {before.Version}");
		await LinuxHost.ReplaceLinkAsync(Paths.Current, Paths.Release(before.Version)).ConfigureAwait(false);
		Restore(Paths.ReleaseLog, before.ReleaseLog);
		Restore(Paths.AgentUnit, before.AgentUnit);
		Restore(Paths.PanelUnit, before.PanelUnit);
		await this.host.SystemctlAsync("daemon-reload").ConfigureAwait(false);
		if (before.AgentUnit is null || !(await this.host.SystemctlAsync("restart", InstallPaths.AgentUnitName).ConfigureAwait(false)).Succeeded)
		{
			return false;
		}

		// Панель остановилась вместе с демоном (Requires=); --no-block — как при обычном перезапуске.
		if (before.PanelUnit is not null)
		{
			await this.host.SystemctlAsync("restart", "--no-block", InstallPaths.PanelUnitName).ConfigureAwait(false);
		}

		Output.WriteLine($"Active version: {before.Version}");
		return true;
	}

	private static void Restore(string path, string? content)
	{
		if (content is null)
		{
			Files.Delete(path);
		}
		else
		{
			LinuxHost.WriteFile(path, content, readable);
		}
	}

	/// <summary>Хранятся текущая и две предыдущие версии; остальные удаляются.</summary>
	private Task PruneAsync(string current)
	{
		var history = ReleaseHistory.Parse(ReadOrEmpty(Paths.ReleaseLog));
		foreach (var obsolete in history.Obsolete(InstalledVersions(), current))
		{
			Directory.Delete(Paths.Release(obsolete), recursive: true);
			Output.WriteLine($"Removed old version {obsolete}");
		}

		return Task.CompletedTask;
	}

	/// <summary>
	/// <summary>
	/// sudo-правило и привилегированный помощник прежней версии панели. Панели sudo не нужен,
	/// а правило, открывающее её учётной записи доступ к root, осталось бы после
	/// обновления.
	/// </summary>
	private void RemoveLegacyPrivilege()
	{
		foreach (var path in new[] { Paths.LegacySudoers, Paths.LegacyHelper, Paths.LegacyClaim })
		{
			if (File.Exists(path))
			{
				File.Delete(path);
				Output.WriteLine($"Removed file from an older version: {path}");
			}
		}
	}

	private void SeedRegistry()
	{
		Directory.CreateDirectory(Paths.RegistryDirectory);
		File.SetUnixFileMode(Paths.RegistryDirectory, executable);
		var decision = RegistrySeed.Decide(
			File.Exists(Paths.Registry),
			File.Exists(Paths.PanelConfig) ? File.ReadAllText(Paths.PanelConfig) : null,
			NonEmpty(Paths.LegacyServerConfig),
			Templates.LegacyRegistry);
		if (!decision.IsSuccess)
		{
			throw new InstallFailed(decision.Failure!.Message);
		}

		switch (decision.Value)
		{
			case RegistryDecision.Keep:
				Output.WriteLine("The existing server registry was preserved.");
				break;
			case RegistryDecision.MigrateFlat migrate:
				LinuxHost.WriteFile(Paths.Registry, migrate.Json, readable);
				Output.WriteLine($"Server main was added to {Paths.Registry}");
				break;
			case RegistryDecision.AdoptLegacy adopt:
				LinuxHost.WriteFile(Paths.Registry, adopt.Json, readable);
				Output.WriteLine("An existing server was found in /srv/vintagestory and registered as main.");
				break;
			case RegistryDecision.CreateEmpty empty:
				LinuxHost.WriteFile(Paths.Registry, empty.Json, readable);
				break;
		}
	}

	/// <summary>
	/// Мод телеметрии: копия рядом с панелью, из которой демон кладёт мод в каждый новый сервер,
	/// и, если есть сервер в /srv/vintagestory, в его каталог модов (прежний архив — в <c>.history</c>).
	/// </summary>
	private void InstallTelemetry(Artifact artifact, string modDirectory)
	{
		if (artifact.NewestTelemetry is not { } archive)
		{
			this.host.Warn("the telemetry mod is not included in this build, so server readiness checks will be limited. Build the mod and place it in the build's telemetry/ directory (VS_TELEMETRY_DIST for scripts/publish.sh).");
			return;
		}

		LinuxHost.CreateDirectory(Paths.SharedData, executable);
		LinuxHost.CreateDirectory(Paths.TelemetryDirectory, executable);
		LinuxHost.CopyFile(archive, Path.Combine(Paths.TelemetryDirectory, Path.GetFileName(archive)), readable);

		if (modDirectory == Paths.LegacyModsDirectory && !Directory.Exists(Path.GetDirectoryName(modDirectory)))
		{
			return;
		}

		LinuxHost.CreateDirectory(modDirectory, executable);
		var history = Path.Combine(modDirectory, ".history", "vspaneltelemetry", "install-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
		foreach (var old in Directory.GetFiles(modDirectory, "vspaneltelemetry_*.zip"))
		{
			Directory.CreateDirectory(history);
			File.Move(old, Path.Combine(history, Path.GetFileName(old)));
		}

		LinuxHost.CopyFile(archive, Path.Combine(modDirectory, Path.GetFileName(archive)), readable);
		Output.WriteLine($"Telemetry mod {Path.GetFileName(archive)} installed in {modDirectory}");
	}

	/// <summary>
	/// Первый сервер с настройками мастера, только при пустом реестре и незавершённом мастере.
	/// Ошибка не прерывает установку: сервер можно создать в мастере. Возвращает порт
	/// созданного сервера или 0.
	/// </summary>
	private async Task<int> CreateFirstServerAsync(InstallOptions options, bool dotnetReady)
	{
		if (this.host.SetupCompleted() || this.host.RegisteredServers() is not 0)
		{
			return 0;
		}

		this.host.Step("Creating a game server with default settings");
		if (!dotnetReady)
		{
			this.host.Warn(".NET runtime is missing, so no server was created. Install .NET, then create the server in the setup wizard");
			return 0;
		}

		if (await this.host.IsActiveAsync(InstallPaths.PanelUnitName).ConfigureAwait(false)
			&& MaintenanceProbe.Describe(this.host.StateDirectory()) is not null)
		{
			this.host.Warn("the panel is already creating a server through the setup wizard; automatic creation was skipped");
			return 0;
		}

		// Сервер создаётся при остановленной панели, чтобы не вмешались ни мастер, ни расписание.
		await this.host.SystemctlAsync("stop", InstallPaths.PanelUnitName).ConfigureAwait(false);
		var arguments = new List<string> { "provision" };
		if (options.GameVersion.Length > 0)
		{
			arguments.AddRange(["--version", options.GameVersion]);
		}

		if (options.GamePort > 0)
		{
			arguments.AddRange(["--port", options.GamePort.ToString(CultureInfo.InvariantCulture)]);
		}

		if (await this.host.ManageAsync(arguments).ConfigureAwait(false) != 0)
		{
			this.host.Warn("the server was not created automatically; create it in the setup wizard using the link below");
			return 0;
		}

		var port = options.GamePort > 0 ? options.GamePort : 42420;
		await OpenGamePortAsync(port).ConfigureAwait(false);
		return port;
	}

	private async Task OpenGamePortAsync(int port)
	{
		var status = await Shell.RunAsync("ufw", ["status"]).ConfigureAwait(false);
		if (!status.Succeeded || !status.Output.Contains("Status: active", StringComparison.Ordinal))
		{
			return;
		}

		// Те же правила ставит демон для серверов из панели (GamePortRule): так он снимет и эти.
		foreach (var rule in GamePortRule.UfwAllow(port))
		{
			await Shell.RunAsync("ufw", rule).ConfigureAwait(false);
		}

		Output.WriteLine($"Game port {port.ToString(CultureInfo.InvariantCulture)} is open in ufw (TCP and UDP).");
	}

	private void ReportFirewall(FirewallOutcome? outcome)
	{
		if (outcome is null)
		{
			return;
		}

		if (outcome.Failed)
		{
			this.host.Warn(outcome.Message);
		}
		else
		{
			Output.WriteLine(outcome.Message);
		}
	}

	/// <summary>
	/// Пакеты для среды .NET игры и для <c>setpriv</c>, только Debian/Ubuntu. Из пакетов с
	/// номером версии в имени (<c>libicu74</c>, <c>libssl3t64</c>) берётся самый новый.
	/// </summary>
	private async Task InstallPackagesAsync()
	{
		if (!Shell.Exists("apt-get"))
		{
			Output.WriteLine("Automatic package installation is supported only on Debian/Ubuntu.");
			return;
		}

		var environment = new Dictionary<string, string> { ["DEBIAN_FRONTEND"] = "noninteractive" };
		await Shell.RunAsync("apt-get", ["update", "-qq"], TimeSpan.FromMinutes(10), environment).ConfigureAwait(false);
		var names = (await Shell.RunAsync("apt-cache", ["pkgnames"], TimeSpan.FromMinutes(2)).ConfigureAwait(false)).Output
			.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		var packages = new List<string> { "ca-certificates", "libstdc++6", "zlib1g", "util-linux" };
		packages.AddRange(new[] { PackageNames.Icu, PackageNames.Ssl }.Select(pattern => PackageNames.Newest(names, pattern)).OfType<string>());

		var installed = await Shell.RunAsync(
				"apt-get",
				["install", "-y", "-qq", "--no-install-recommends", .. packages],
				TimeSpan.FromMinutes(20),
				environment)
			.ConfigureAwait(false);
		if (!installed.Succeeded)
		{
			this.host.Warn($"packages were not installed: {installed.Reason}");
		}
	}

	/// <summary>
	/// Среда .NET для игры через официальный <c>dotnet-install.sh</c>, только среда выполнения.
	/// Панели она не нужна: панель и демон self-contained.
	/// </summary>
	private async Task<bool> InstallDotnetAsync(string channel)
	{
		var major = channel.Split('.')[0];
		foreach (var binary in new[] { "/usr/bin/dotnet", "/usr/local/bin/dotnet", "/usr/share/dotnet/dotnet", "/usr/lib/dotnet/dotnet" })
		{
			if (File.Exists(binary)
				&& (await Shell.RunAsync(binary, ["--list-runtimes"]).ConfigureAwait(false)).Output
					.Split('\n').Any(line => line.StartsWith($"Microsoft.NETCore.App {major}.", StringComparison.Ordinal)))
			{
				Output.WriteLine($".NET {channel} runtime is already installed: {binary}");
				return true;
			}
		}

		var script = Path.Combine(Path.GetTempPath(), $"dotnet-install-{Guid.NewGuid():N}.sh");
		try
		{
			using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) })
			{
				await File.WriteAllBytesAsync(script, await client.GetByteArrayAsync(new Uri("https://dot.net/v1/dotnet-install.sh")).ConfigureAwait(false)).ConfigureAwait(false);
			}

			var installed = await Shell.RunAsync(
					"bash",
					[script, "--runtime", "dotnet", "--channel", channel, "--install-dir", "/usr/share/dotnet", "--no-path"],
					TimeSpan.FromMinutes(15))
				.ConfigureAwait(false);
			if (!installed.Succeeded)
			{
				this.host.Warn($".NET {channel} installation failed: {installed.Reason}");
				return false;
			}
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
		{
			this.host.Warn($"could not download dotnet-install.sh ({exception.Message}); install .NET {channel} manually");
			return false;
		}
		finally
		{
			File.Delete(script);
		}

		await Shell.RunAsync("chmod", ["-R", "a+rX", "/usr/share/dotnet"]).ConfigureAwait(false);
		if (!File.Exists("/usr/bin/dotnet"))
		{
			File.CreateSymbolicLink("/usr/bin/dotnet", "/usr/share/dotnet/dotnet");
		}

		Output.WriteLine($".NET {channel} runtime installed in /usr/share/dotnet");
		return true;
	}

	private void PrintSummary(int serverPort)
	{
		Output.WriteLine("\nPanel installed.");
		PanelAccess.Print(Output, EnvironmentFile.Load(Paths.PanelEnv), PanelAccess.MachineAddresses(), tunnel: true);
		if (serverPort > 0)
		{
			Output.WriteLine($"Game server main was created and started on port {serverPort.ToString(CultureInfo.InvariantCulture)}.");
		}

		Output.WriteLine("\nCommands: sudo vs-panel help");
	}

	private static async Task Require(Task<ShellResult> command, string failure)
	{
		var result = await command.ConfigureAwait(false);
		if (!result.Succeeded)
		{
			throw new InstallFailed($"{failure}: {result.Reason}");
		}
	}

	/// <summary>Прежняя версия ставила <c>/usr/local/bin/vs-panel</c> обычным файлом; на его место встанет ссылка.</summary>
	private static void DeleteRegularFile(string path)
	{
		var info = new FileInfo(path);
		if (info.Exists && info.LinkTarget is null)
		{
			info.Delete();
		}
	}

	private static bool NonEmpty(string path)
	{
		var info = new FileInfo(path);
		return info.Exists && info.Length > 0;
	}

	private static string ReadOrEmpty(string path) => File.Exists(path) ? File.ReadAllText(path) : string.Empty;

	private static string? ReadOrNull(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

	private bool Report(string message, bool value)
	{
		Output.WriteLine(message);
		return value;
	}

	/// <summary>Версия до установки и файлы, которые установка перезаписывает под новую.</summary>
	private sealed record ReleaseSnapshot(string Version, string? ReleaseLog, string? AgentUnit, string? PanelUnit);
}
