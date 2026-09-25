using System.Globalization;
using System.Runtime.Versioning;

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
			throw new InstallFailed("Нужен systemd.");
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
			throw new InstallFailed($"Установка не начата: панель выполняет {busy}.\nДождитесь завершения операции в панели и повторите установку.");
		}

		var dotnetReady = true;
		if (!options.Repair)
		{
			if (!options.SkipPackages)
			{
				this.host.Step("Установка системных пакетов");
				await InstallPackagesAsync().ConfigureAwait(false);
			}

			this.host.Step($"Установка среды выполнения .NET {options.DotnetChannel} для игры");
			dotnetReady = options.SkipDotnet
				? Report("Пропущено по VS_PANEL_SKIP_DOTNET=1.", true)
				: await InstallDotnetAsync(options.DotnetChannel).ConfigureAwait(false);
		}

		this.host.Step(options.Repair ? $"Восстановление версии {artifact.Value.Version}" : $"Установка версии {artifact.Value.Version}");
		var uid = await this.host.EnsurePanelUserAsync().ConfigureAwait(false);
		LinuxHost.CreateDirectory(Paths.ProgramRoot, executable);
		LinuxHost.CreateDirectory(Paths.Releases, executable);
		LinuxHost.CreateDirectory(this.host.StateDirectory(), @private);
		LinuxHost.CreateDirectory(Path.Combine(this.host.StateDirectory(), "staging"), @private);
		if (!options.Repair)
		{
			PlaceRelease(artifact.Value);
		}

		await ActivateAsync(artifact.Value.Version).ConfigureAwait(false);
		await PruneAsync(artifact.Value.Version).ConfigureAwait(false);

		this.host.Step("Команда vs-panel, обёртка консоли, юниты");
		Directory.CreateDirectory(Path.GetDirectoryName(Paths.CommandLink)!);
		DeleteRegularFile(Paths.CommandLink);
		await LinuxHost.ReplaceLinkAsync(Paths.CommandLink, Paths.CurrentBinary("vs-panel")).ConfigureAwait(false);
		Directory.CreateDirectory(Path.GetDirectoryName(Paths.ConsoleWrapper)!);
		LinuxHost.WriteFile(Paths.ConsoleWrapper, Templates.ConsoleWrapper, executable);
		RemoveLegacyPrivilege();

		SeedRegistry();
		LinuxHost.WriteFile(Paths.PanelConfig, Templates.KeepChosenPaths(Templates.PanelConfig, File.Exists(Paths.PanelConfig) ? File.ReadAllText(Paths.PanelConfig) : null), readable);
		if (!File.Exists(Paths.PanelEnv))
		{
			LinuxHost.WriteFile(Paths.PanelEnv, Templates.PanelEnvironment, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
			await this.host.ChownAsync($"root:{InstallPaths.PanelUser}", Paths.PanelEnv).ConfigureAwait(false);
		}

		LinuxHost.WriteFile(Paths.AgentUnit, Templates.AgentUnit(uid), readable);
		LinuxHost.WriteFile(Paths.PanelUnit, Templates.PanelUnit, readable);
		if (!options.Repair)
		{
			InstallTelemetry(artifact.Value, options.TelemetryModDirectory);
		}

		this.host.Step("Демон панели");
		await this.host.ChownAsync($"{InstallPaths.PanelUser}:{InstallPaths.PanelUser}", this.host.StateDirectory(), recursive: true).ConfigureAwait(false);
		await Require(this.host.SystemctlAsync("daemon-reload"), "systemctl daemon-reload").ConfigureAwait(false);
		await Require(this.host.SystemctlAsync("enable", InstallPaths.AgentUnitName), "Демон не включён").ConfigureAwait(false);

		// Демон нужен раньше панели: через него создаётся первый сервер. Панель с Requires= на
		// демон systemd перезапустит вместе с ним.
		await Require(this.host.SystemctlAsync("restart", InstallPaths.AgentUnitName), "Демон не запустился").ConfigureAwait(false);

		this.host.Step("Инициализация состояния");
		if (await this.host.ManageAsync(["init"]).ConfigureAwait(false) != 0)
		{
			throw new InstallFailed("Состояние панели не инициализировано");
		}

		var setupLink = string.Empty;
		var state = this.host.StateDirectory();
		if (NonEmpty(Path.Combine(state, "accounts.json")) || NonEmpty(Path.Combine(state, "admin.password")))
		{
			Output.WriteLine("Существующая учётная запись администратора сохранена.");
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

		this.host.Step("Перезапуск панели");
		await Require(this.host.SystemctlAsync("enable", InstallPaths.PanelUnitName), "Панель не включена").ConfigureAwait(false);

		// `enable --now` не перезапускает уже активный юнит, и после обновления работала бы старая
		// версия. --no-block нужен, чтобы установщик не зависал, пока старая панель завершает
		// долгую операцию.
		var oldPid = await this.host.UnitPropertyAsync(InstallPaths.PanelUnitName, "MainPID").ConfigureAwait(false);
		await Require(this.host.SystemctlAsync("restart", "--no-block", InstallPaths.PanelUnitName), "Перезапуск панели не принят").ConfigureAwait(false);
		var started = await this.host.WaitForStartAsync(InstallPaths.PanelUnitName, oldPid, options.WaitSeconds).ConfigureAwait(false);

		PrintSummary(created, setupLink.Length > 0);
		if (setupLink.Length > 0)
		{
			Output.WriteLine();
			await this.host.ManageAsync(["claim-token"]).ConfigureAwait(false);
			Output.WriteLine("\nЕсли ссылка потеряна, получите новую: sudo vs-panel access");
		}

		return started ? 0 : 1;
	}

	/// <summary>Вернуть предыдущую версию: замена ссылки, затем перезапуск демона и панели.</summary>
	public async Task<int> RollbackAsync(int waitSeconds)
	{
		var current = CurrentVersion() ?? throw new InstallFailed("Активная версия не найдена: панель не установлена");
		var history = ReleaseHistory.Parse(ReadOrEmpty(Paths.ReleaseLog));
		var previous = history.Previous(current, InstalledVersions())
			?? throw new InstallFailed($"Откатываться некуда: кроме {current} установленных версий нет");

		if (await this.host.IsActiveAsync(InstallPaths.PanelUnitName).ConfigureAwait(false)
			&& MaintenanceProbe.Describe(this.host.StateDirectory()) is { } busy)
		{
			throw new InstallFailed($"Откат не начат: панель выполняет {busy}.");
		}

		this.host.Step($"Откат {current} → {previous}");
		await ActivateAsync(previous).ConfigureAwait(false);
		var oldPid = await this.host.UnitPropertyAsync(InstallPaths.PanelUnitName, "MainPID").ConfigureAwait(false);
		await Require(this.host.SystemctlAsync("restart", InstallPaths.AgentUnitName), "Демон не запустился").ConfigureAwait(false);
		await Require(this.host.SystemctlAsync("restart", "--no-block", InstallPaths.PanelUnitName), "Перезапуск панели не принят").ConfigureAwait(false);
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

		Output.WriteLine($"Версия {artifact.Version} скопирована в {target}");
	}

	private async Task ActivateAsync(string version)
	{
		await LinuxHost.ReplaceLinkAsync(Paths.Current, Paths.Release(version)).ConfigureAwait(false);
		var history = ReleaseHistory.Parse(ReadOrEmpty(Paths.ReleaseLog)).Activate(version);
		LinuxHost.WriteFile(Paths.ReleaseLog, history.Serialize(), readable);
		Output.WriteLine($"Активная версия: {version}");
	}

	/// <summary>Хранятся текущая и две предыдущие версии; остальные удаляются.</summary>
	private Task PruneAsync(string current)
	{
		var history = ReleaseHistory.Parse(ReadOrEmpty(Paths.ReleaseLog));
		foreach (var obsolete in history.Obsolete(InstalledVersions(), current))
		{
			Directory.Delete(Paths.Release(obsolete), recursive: true);
			Output.WriteLine($"Удалена старая версия {obsolete}");
		}

		return Task.CompletedTask;
	}

	/// <summary>
	/// <summary>
	/// sudo-правило и привилегированный помощник прежней версии панели. Панели sudo не нужен
	/// (ADR-02, INV-51), а правило, открывающее её учётной записи доступ к root, осталось бы после
	/// обновления.
	/// </summary>
	private void RemoveLegacyPrivilege()
	{
		foreach (var path in new[] { Paths.LegacySudoers, Paths.LegacyHelper, Paths.LegacyClaim })
		{
			if (File.Exists(path))
			{
				File.Delete(path);
				Output.WriteLine($"Удалён файл прежней версии: {path}");
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
				Output.WriteLine("Существующий реестр серверов сохранён.");
				break;
			case RegistryDecision.MigrateFlat migrate:
				LinuxHost.WriteFile(Paths.Registry, migrate.Json, readable);
				Output.WriteLine($"Сервер main перенесён в {Paths.Registry}");
				break;
			case RegistryDecision.AdoptLegacy adopt:
				LinuxHost.WriteFile(Paths.Registry, adopt.Json, readable);
				Output.WriteLine("Обнаружен существующий сервер в /srv/vintagestory: он записан в реестр как main.");
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
			this.host.Warn("мод телеметрии не входит в сборку — готовность серверов будет проверяться ограниченно. Соберите его и положите в telemetry/ сборки (VS_TELEMETRY_DIST при scripts/publish.sh).");
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
		Output.WriteLine($"Мод телеметрии {Path.GetFileName(archive)} установлен в {modDirectory}");
	}

	/// <summary>
	/// Первый сервер с настройками мастера, только при пустом реестре и незавершённом мастере
	/// (INV-45). Ошибка не прерывает установку: сервер можно создать в мастере. Возвращает порт
	/// созданного сервера или 0.
	/// </summary>
	private async Task<int> CreateFirstServerAsync(InstallOptions options, bool dotnetReady)
	{
		if (this.host.SetupCompleted() || this.host.RegisteredServers() is not 0)
		{
			return 0;
		}

		this.host.Step("Создание игрового сервера с настройками по умолчанию");
		if (!dotnetReady)
		{
			this.host.Warn("нет среды .NET — сервер не создан, создайте его в мастере установки после установки .NET");
			return 0;
		}

		if (await this.host.IsActiveAsync(InstallPaths.PanelUnitName).ConfigureAwait(false)
			&& MaintenanceProbe.Describe(this.host.StateDirectory()) is not null)
		{
			this.host.Warn("панель уже создаёт сервер через мастер установки — автоматическое создание пропущено");
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
			this.host.Warn("сервер не создан автоматически — создайте его в мастере установки по ссылке ниже");
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

		var text = port.ToString(CultureInfo.InvariantCulture);
		await Shell.RunAsync("ufw", ["allow", text + "/tcp"]).ConfigureAwait(false);
		await Shell.RunAsync("ufw", ["allow", text + "/udp"]).ConfigureAwait(false);
		Output.WriteLine($"В ufw открыт игровой порт {text} (tcp и udp).");
	}

	/// <summary>
	/// Пакеты для среды .NET игры и для <c>setpriv</c>, только Debian/Ubuntu. Из пакетов с
	/// номером версии в имени (<c>libicu74</c>, <c>libssl3t64</c>) берётся самый новый.
	/// </summary>
	private async Task InstallPackagesAsync()
	{
		if (!Shell.Exists("apt-get"))
		{
			Output.WriteLine("Автоматическая установка пакетов поддерживается только на Debian/Ubuntu.");
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
			this.host.Warn($"пакеты не установлены: {installed.Reason}");
		}
	}

	/// <summary>
	/// Среда .NET для игры через официальный <c>dotnet-install.sh</c>, только среда выполнения.
	/// Панели она не нужна: панель и демон self-contained (ADR-09).
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
				Output.WriteLine($"Среда .NET {channel} уже установлена: {binary}");
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
				this.host.Warn($"установка .NET {channel} не удалась: {installed.Reason}");
				return false;
			}
		}
		catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
		{
			this.host.Warn($"не удалось скачать dotnet-install.sh ({exception.Message}); установите .NET {channel} вручную");
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

		Output.WriteLine($"Установлена среда .NET {channel} в /usr/share/dotnet");
		return true;
	}

	private void PrintSummary(int serverPort, bool claimPending)
	{
		var environment = EnvironmentFile.Load(Paths.PanelEnv);
		var bind = EnvironmentFile.Value(environment, "PANEL_BIND", "127.0.0.1");
		var port = EnvironmentFile.Value(environment, "PANEL_PORT", "8080");
		Output.WriteLine($"\nПанель установлена и слушает {bind}:{port}.");
		if (serverPort > 0)
		{
			Output.WriteLine($"Игровой сервер main создан и запущен, порт {serverPort.ToString(CultureInfo.InvariantCulture)}.");
		}

		if (bind is "127.0.0.1" or "localhost" or "::1")
		{
			Output.WriteLine($"С рабочего компьютера откройте туннель: ssh -L {port}:127.0.0.1:{port} user@server");
		}

		if (!claimPending)
		{
			Output.WriteLine($"Затем откройте http://127.0.0.1:{port}");
		}

		Output.WriteLine("\nУправление: sudo vs-panel help");
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

	private bool Report(string message, bool value)
	{
		Output.WriteLine(message);
		return value;
	}
}
