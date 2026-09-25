using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Panel.Fleet.Domain;
using Panel.Fleet.Infrastructure;

namespace Panel.Cli;

/// <summary>
/// <c>vs-panel install | update | repair | rollback</c> на Windows: две службы.
/// </summary>
/// <remarks>
/// <para>
/// Порядок тот же, что у <see cref="Installer"/>: отказ поверх идущей операции, реестр
/// пишется один раз, ключ сессий и учётные записи сохраняются при обновлении, демон
/// запускается раньше панели, первый сервер создаётся при остановленной панели и его ошибка
/// не прерывает установку, в конце выдаётся одноразовая ссылка мастера. Форматы состояния
/// знает только <c>vs-panel-web manage</c>.
/// </para>
/// <para>
/// Отличие от Linux: службы останавливаются до копирования версии. Windows не даёт
/// переименовать каталог с запущенным исполняемым файлом, и переустановка активной версии
/// поверх работающей службы сорвалась бы на полпути. Остановка демона не останавливает
/// игровые серверы (§65), но их консоль недоступна до перезапуска сервера.
/// </para>
/// <para>
/// Службы работают от LocalSystem: пароль не спрашивается, учётная запись пользователя и
/// политика безопасности не меняются (§69).
/// </para>
/// <para>
/// Только на Windows: список доступа каталога данных сужается до SYSTEM и администраторов.
/// <c>ProgramData</c> по умолчанию доступен на чтение всем, и ключ сессий и учётные записи
/// панели мог бы прочитать любой пользователь машины (§66).
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsInstaller
{
	/// <summary>Сколько ждать запуска демона; канал открывается за секунды.</summary>
	private const int agentWaitSeconds = 60;

	private readonly WindowsHost host;

	public WindowsInstaller(WindowsHost host) => this.host = host;

	private WindowsPaths Paths => this.host.Paths;

	private TextWriter Output => this.host.Output;

	public async Task<int> InstallAsync(InstallOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		var artifact = Artifact.Open(options.From, windows: true);
		if (!artifact.IsSuccess)
		{
			throw new InstallFailed(artifact.Failure!.Message);
		}

		CheckPlatform(artifact.Value);
		CheckNotRunningFrom(artifact.Value, options.Repair);
		if (WindowsHost.IsRunning(WindowsPaths.PanelService) && MaintenanceProbe.Describe(this.host.StateDirectory()) is { } busy)
		{
			throw new InstallFailed($"Установка не начата: панель выполняет {busy}.\nДождитесь завершения операции в панели и повторите установку.");
		}

		var dotnetReady = true;
		if (!options.Repair)
		{
			this.host.Step($"Среда выполнения .NET {options.DotnetChannel} для игры");
			dotnetReady = options.SkipDotnet
				? Report("Пропущено по VS_PANEL_SKIP_DOTNET=1.", true)
				: await InstallDotnetAsync(options.DotnetChannel).ConfigureAwait(false);
			WarnLongPaths();
		}

		await RetireLegacyTasksAsync().ConfigureAwait(false);

		this.host.Step("Остановка служб панели");
		var servers = this.host.RegisteredServers() ?? 0;
		if (WindowsHost.IsRunning(WindowsPaths.AgentService) && servers > 0)
		{
			this.host.Warn("демон перезапускается: работающие игровые серверы продолжат работу, но их консоль вернётся только после перезапуска сервера.");
		}

		await StopServicesAsync(options.WaitSeconds).ConfigureAwait(false);

		this.host.Step(options.Repair ? $"Восстановление версии {artifact.Value.Version}" : $"Установка версии {artifact.Value.Version}");
		foreach (var directory in new[] { Paths.ProgramRoot, Paths.Releases, Paths.DataRoot, this.host.StateDirectory(), Path.Combine(this.host.StateDirectory(), "staging"), Paths.ServersDirectory, Paths.RunDirectory, Paths.TelemetryDirectory })
		{
			Directory.CreateDirectory(directory);
		}

		if (!options.Repair)
		{
			PlaceRelease(artifact.Value);
		}

		Activate(artifact.Value.Version);
		Prune(artifact.Value.Version);
		AddToPath();

		this.host.Step("Настройки, реестр, права");
		SeedRegistry();
		WriteFile(Paths.PanelConfig, Templates.KeepChosenPaths(Templates.WindowsPanelConfig(Paths.DataRoot), ReadOrNull(Paths.PanelConfig)));
		if (!File.Exists(Paths.PanelEnv))
		{
			WriteFile(Paths.PanelEnv, Templates.WindowsPanelEnvironment(Paths.DataRoot));
		}

		RestrictDataAccess();
		if (!options.Repair)
		{
			await InstallTelemetryAsync(artifact.Value).ConfigureAwait(false);
		}

		this.host.Step("Службы");
		RegisterServices();

		// Демон нужен раньше панели: через него создаётся первый сервер.
		if (!await this.host.StartAsync(WindowsPaths.AgentService, agentWaitSeconds).ConfigureAwait(false))
		{
			throw new InstallFailed("Демон не запустился — установка остановлена. Проверьте: vs-panel doctor");
		}

		this.host.Step("Инициализация состояния");
		if ((await this.host.ManageAsync(["init"]).ConfigureAwait(false)).ExitCode != 0)
		{
			throw new InstallFailed("Состояние панели не инициализировано");
		}

		var claimPending = !PanelState.HasAdministrator(this.host.StateDirectory());
		if (!claimPending)
		{
			Output.WriteLine("Существующая учётная запись администратора сохранена.");
		}

		var rootReady = await ServersRootStep.RunAsync(
				options,
				Paths.PanelConfig,
				firstInstall: !options.Repair && this.host.RegisteredServers() is 0,
				new ServersRootStep.Terminal(Output, Console.IsInputRedirected ? null : Console.In, this.host.Step, this.host.Warn, start => FolderPicker.PickAsync(ServersRootStep.BrowseTitle, start)),
				async command => (await this.host.ManageAsync(command).ConfigureAwait(false)).ExitCode == 0)
			.ConfigureAwait(false);
		var created = options.Repair || !options.CreateServer || !rootReady ? 0 : await CreateFirstServerAsync(options, dotnetReady).ConfigureAwait(false);

		this.host.Step("Запуск панели");
		var started = await this.host.StartAsync(WindowsPaths.PanelService, options.WaitSeconds).ConfigureAwait(false);
		WriteShortcuts();

		PrintSummary(created);
		if (claimPending && started)
		{
			Output.WriteLine();
			var claim = await this.host.ManageAsync(["claim-token"]).ConfigureAwait(false);
			if (options.OpenBrowser && PanelState.ClaimLink(claim.Output) is { } link)
			{
				await this.host.OpenAsync(link).ConfigureAwait(false);
			}

			Output.WriteLine("\nЕсли ссылка потеряна, получите новую: vs-panel access (в командной строке администратора)");
		}
		else if (started && options.OpenBrowser)
		{
			// Администратор уже есть, ссылка мастера не нужна, но после установки всё равно
			// открывается страница панели.
			await this.host.OpenAsync(PanelState.LocalAddress(this.host.PanelEnvironment())).ConfigureAwait(false);
		}

		return started ? 0 : 1;
	}

	/// <summary>Вернуть предыдущую версию: остановка, замена ссылки, запуск демона и панели.</summary>
	public async Task<int> RollbackAsync(int waitSeconds)
	{
		var current = CurrentVersion() ?? throw new InstallFailed("Активная версия не найдена: панель не установлена");
		var history = ReleaseHistory.Parse(ReadOrEmpty(Paths.ReleaseLog));
		var previous = history.Previous(current, InstalledVersions())
			?? throw new InstallFailed($"Откатываться некуда: кроме {current} установленных версий нет");

		if (WindowsHost.IsRunning(WindowsPaths.PanelService) && MaintenanceProbe.Describe(this.host.StateDirectory()) is { } busy)
		{
			throw new InstallFailed($"Откат не начат: панель выполняет {busy}.");
		}

		this.host.Step($"Откат {current} → {previous}");
		await StopServicesAsync(waitSeconds).ConfigureAwait(false);
		Activate(previous);
		var agent = await this.host.StartAsync(WindowsPaths.AgentService, agentWaitSeconds).ConfigureAwait(false);
		var panel = agent && await this.host.StartAsync(WindowsPaths.PanelService, waitSeconds).ConfigureAwait(false);
		return panel ? 0 : 1;
	}

	/// <summary>Версия, на которую указывает <c>current</c>.</summary>
	public string? CurrentVersion()
	{
		try
		{
			return new DirectoryInfo(Paths.Current).LinkTarget is { } target ? Path.GetFileName(target.TrimEnd('\\', '/')) : null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	public IReadOnlyCollection<string> InstalledVersions() =>
		Directory.Exists(Paths.Releases)
			? [.. Directory.GetDirectories(Paths.Releases).Select(Path.GetFileName).OfType<string>().Where(ReleaseHistory.IsVersion)]
			: [];

	/// <summary>Сборка должна быть для этой машины: RID Windows той же архитектуры.</summary>
	private static void CheckPlatform(Artifact artifact)
	{
		var machine = RuntimeInformation.OSArchitecture switch
		{
			Architecture.X64 => "win-x64",
			Architecture.Arm64 => "win-arm64",
			_ => throw new InstallFailed($"Архитектура {RuntimeInformation.OSArchitecture} не поддерживается"),
		};

		if (artifact.Rid.Length > 0 && artifact.Rid != machine)
		{
			throw new InstallFailed($"Сборка для {artifact.Rid}, а эта машина — {machine}: bash scripts/publish.sh {machine}");
		}
	}

	/// <summary>
	/// Переустановка версии, из каталога которой запущен сам установщик, заменила бы каталог с
	/// запущенным файлом, а Windows этого не позволяет.
	/// </summary>
	private void CheckNotRunningFrom(Artifact artifact, bool repair)
	{
		if (repair || Environment.ProcessPath is not { } process)
		{
			return;
		}

		var running = Path.GetFullPath(process);
		foreach (var directory in new[] { Paths.Release(artifact.Version), Paths.Current })
		{
			if (CurrentVersion() == artifact.Version && running.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			{
				throw new InstallFailed($"Версия {artifact.Version} уже установлена, и установщик запущен из неё. Переустановите из каталога сборки (install.cmd) или восстановите: vs-panel repair");
			}
		}
	}

	private void RegisterServices()
	{
		ServiceManager.Install(
			new ServiceDefinition(
				WindowsPaths.AgentService,
				"Vintage Story Panel — демон",
				"Держит игровые серверы Vintage Story и выполняет команды панели через именованный канал.",
				Paths.AgentCommandLine,
				[]));

		// Зависимость работает как Requires= юнита панели: диспетчер запускает демон раньше
		// панели, а остановка демона останавливает и её.
		ServiceManager.Install(
			new ServiceDefinition(
				WindowsPaths.PanelService,
				"Vintage Story Panel",
				"Веб-панель управления серверами Vintage Story.",
				Paths.PanelCommandLine,
				[WindowsPaths.AgentService]));
		Output.WriteLine($"Службы {WindowsPaths.AgentService} и {WindowsPaths.PanelService} зарегистрированы: запуск автоматический, учётная запись LocalSystem.");
	}

	private async Task StopServicesAsync(int waitSeconds)
	{
		// Демон сам останавливает зависимую панель; отдельная остановка панели даёт ей своё время
		// на завершение операции.
		if (WindowsHost.Status(WindowsPaths.PanelService) is not null && !await this.host.StopAsync(WindowsPaths.PanelService, waitSeconds).ConfigureAwait(false))
		{
			throw new InstallFailed("Панель не остановилась — установка не начата. Повторите позже.");
		}

		if (WindowsHost.Status(WindowsPaths.AgentService) is not null && !await this.host.StopAsync(WindowsPaths.AgentService, agentWaitSeconds).ConfigureAwait(false))
		{
			throw new InstallFailed("Демон не остановился — установка не начата.");
		}
	}

	/// <summary>
	/// Задания планировщика прежней версии панели заняли бы порт панели при загрузке. Её файлы
	/// остаются на месте, о них напомнит <c>doctor</c>.
	/// </summary>
	private async Task RetireLegacyTasksAsync()
	{
		foreach (var task in WindowsPaths.LegacyTasks)
		{
			if (!(await Shell.RunAsync("schtasks.exe", ["/Query", "/TN", task]).ConfigureAwait(false)).Succeeded)
			{
				continue;
			}

			await Shell.RunAsync("schtasks.exe", ["/End", "/TN", task]).ConfigureAwait(false);
			var deleted = await Shell.RunAsync("schtasks.exe", ["/Delete", "/TN", task, "/F"]).ConfigureAwait(false);
			if (deleted.Succeeded)
			{
				Output.WriteLine($"Удалена задача планировщика прежней версии: {task}");
			}
			else
			{
				this.host.Warn($"задача прежней версии {task} не удалена: {deleted.Reason}");
			}
		}
	}

	/// <summary>Копирование сборки в <c>releases\&lt;версия&gt;</c>: во временный каталог, затем переименование.</summary>
	private void PlaceRelease(Artifact artifact)
	{
		var target = Paths.Release(artifact.Version);
		if (string.Equals(Path.GetFullPath(artifact.Directory).TrimEnd('\\'), Path.GetFullPath(target).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		var staging = target + ".new";
		DeleteDirectory(staging);
		Directory.CreateDirectory(staging);
		foreach (var binary in Artifact.Binaries)
		{
			File.Copy(artifact.Source(binary), Path.Combine(staging, artifact.Installed(binary)), overwrite: true);
		}

		File.WriteAllText(Path.Combine(staging, Artifact.VersionFile), artifact.Version + "\n");
		if (artifact.Rid.Length > 0)
		{
			File.WriteAllText(Path.Combine(staging, Artifact.RidFile), artifact.Rid + "\n");
		}

		if (Directory.Exists(target))
		{
			var retired = target + ".old";
			DeleteDirectory(retired);
			Directory.Move(target, retired);
			Directory.Move(staging, target);
			DeleteDirectory(retired);
		}
		else
		{
			Directory.Move(staging, target);
		}

		Output.WriteLine($"Версия {artifact.Version} скопирована в {target}");
	}

	/// <summary>
	/// <c>current</c> — символическая ссылка на каталог версии. Службы в этот момент остановлены,
	/// поэтому замену в два шага (удалить, создать) никто не заметит.
	/// </summary>
	private void Activate(string version)
	{
		var link = new DirectoryInfo(Paths.Current);
		if (link.Exists || link.LinkTarget is not null)
		{
			if (link.LinkTarget is null)
			{
				throw new InstallFailed($"{Paths.Current} — обычный каталог, а не ссылка на версию. Удалите его и повторите установку.");
			}

			link.Delete();
		}

		Directory.CreateSymbolicLink(Paths.Current, Paths.Release(version));
		var history = ReleaseHistory.Parse(ReadOrEmpty(Paths.ReleaseLog)).Activate(version);
		File.WriteAllText(Paths.ReleaseLog, history.Serialize());
		Output.WriteLine($"Активная версия: {version}");
	}

	/// <summary>Хранятся текущая и две предыдущие версии; остальные удаляются.</summary>
	private void Prune(string current)
	{
		var history = ReleaseHistory.Parse(ReadOrEmpty(Paths.ReleaseLog));
		foreach (var obsolete in history.Obsolete(InstalledVersions(), current))
		{
			try
			{
				Directory.Delete(Paths.Release(obsolete), recursive: true);
				Output.WriteLine($"Удалена старая версия {obsolete}");
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				this.host.Warn($"старая версия {obsolete} не удалена: {exception.Message}");
			}
		}
	}

	/// <summary><c>vs-panel</c> в PATH, как ссылка <c>/usr/local/bin/vs-panel</c> на Linux.</summary>
	private void AddToPath()
	{
		var path = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? string.Empty;
		var entries = path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (entries.Any(entry => string.Equals(entry.TrimEnd('\\'), Paths.Current, StringComparison.OrdinalIgnoreCase)))
		{
			return;
		}

		Environment.SetEnvironmentVariable("PATH", string.Join(';', [.. entries, Paths.Current]), EnvironmentVariableTarget.Machine);
		Output.WriteLine($"Команда vs-panel добавлена в PATH ({Paths.Current}); она появится в новых окнах командной строки.");
	}

	private void SeedRegistry()
	{
		var decision = RegistrySeed.Decide(
			File.Exists(Paths.Registry),
			File.Exists(Paths.PanelConfig) ? File.ReadAllText(Paths.PanelConfig) : null,
			legacyServer: false,
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
				WriteFile(Paths.Registry, migrate.Json);
				Output.WriteLine($"Сервер main перенесён в {Paths.Registry}");
				break;
			case RegistryDecision.CreateEmpty empty:
				WriteFile(Paths.Registry, empty.Json);
				break;
		}
	}

	/// <summary>
	/// Доступ к каталогу данных только у SYSTEM (от неё работают службы и игра) и
	/// администраторов. Наследование от <c>ProgramData</c> отключается; вложенные каталоги,
	/// включая созданные серверы, получают новый список по наследованию. Меняется только этот
	/// каталог.
	/// </summary>
	/// <remarks>
	/// Отдельного права для установившего пользователя нет: игра от SYSTEM запускает файлы из
	/// этого дерева, и право записи без повышения давало бы любой программе в сеансе
	/// пользователя путь к SYSTEM. Проводник откроет каталог после запроса UAC.
	/// </remarks>
	private void RestrictDataAccess()
	{
		var security = new DirectorySecurity();
		security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
		foreach (var sid in new SecurityIdentifier[] { new(WellKnownSidType.LocalSystemSid, null), new(WellKnownSidType.BuiltinAdministratorsSid, null) })
		{
			security.AddAccessRule(new FileSystemAccessRule(
				sid,
				FileSystemRights.FullControl,
				InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
				PropagationFlags.None,
				AccessControlType.Allow));
		}

		new DirectoryInfo(Paths.DataRoot).SetAccessControl(security);
		Output.WriteLine($"Доступ к {Paths.DataRoot}: SYSTEM и администраторы.");
	}

	/// <summary>
	/// Мод телеметрии: копия рядом с данными, из которой демон кладёт мод в каждый новый сервер,
	/// и в каталог модов каждого сервера из реестра (прежний архив — в <c>.history</c>, как на
	/// Linux). Без мода панель не может подтвердить готовность сервера. Работающий сервер
	/// подхватит мод после перезапуска.
	/// </summary>
	private async Task InstallTelemetryAsync(Artifact artifact)
	{
		if (artifact.NewestTelemetry is not { } archive)
		{
			this.host.Warn("мод телеметрии не входит в сборку — готовность серверов будет проверяться ограниченно. Соберите его и положите в telemetry\\ сборки (VS_TELEMETRY_DIST при scripts/publish.sh).");
			return;
		}

		var name = Path.GetFileName(archive);
		File.Copy(archive, Path.Combine(Paths.TelemetryDirectory, name), overwrite: true);
		Output.WriteLine($"Мод телеметрии {name} — в {Paths.TelemetryDirectory}");

		var snapshot = await new FileRegistryStore(Paths.PanelConfig).LoadAsync(CancellationToken.None).ConfigureAwait(false);
		if (snapshot is not RegistrySnapshot.Available available)
		{
			return;
		}

		var updated = 0;
		foreach (var server in available.Servers.Where(server => server.ModsDirectory.Length > 0 && Directory.Exists(server.ModsDirectory)))
		{
			var target = Path.Combine(server.ModsDirectory, name);
			if (File.Exists(target))
			{
				continue;
			}

			var history = Path.Combine(server.ModsDirectory, ".history", "vspaneltelemetry", "install-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
			foreach (var old in Directory.GetFiles(server.ModsDirectory, "vspaneltelemetry_*.zip"))
			{
				Directory.CreateDirectory(history);
				File.Move(old, Path.Combine(history, Path.GetFileName(old)));
			}

			File.Copy(archive, target);
			Output.WriteLine($"Мод телеметрии {name} установлен серверу {server.Id.Value}");
			updated++;
		}

		if (updated > 0)
		{
			Output.WriteLine("Работающие серверы подхватят мод после перезапуска — перезапустите их в панели.");
		}
	}

	/// <summary>
	/// Первый сервер, только при пустом реестре и незавершённом мастере (INV-45). Ошибка не
	/// прерывает установку. Панель в этот момент остановлена, мастер и расписание не вмешаются.
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

		var arguments = new List<string> { "provision" };
		if (options.GameVersion.Length > 0)
		{
			arguments.AddRange(["--version", options.GameVersion]);
		}

		if (options.GamePort > 0)
		{
			arguments.AddRange(["--port", options.GamePort.ToString(CultureInfo.InvariantCulture)]);
		}

		if ((await this.host.ManageAsync(arguments).ConfigureAwait(false)).ExitCode != 0)
		{
			this.host.Warn("сервер не создан автоматически — создайте его в мастере установки по ссылке ниже");
			return 0;
		}

		var port = options.GamePort > 0 ? options.GamePort : 42420;
		await OpenGamePortAsync(port).ConfigureAwait(false);
		return port;
	}

	/// <summary>
	/// Правило брандмауэра Windows для игрового порта, как <c>ufw allow</c> на Linux. Правило с
	/// тем же именем сначала удаляется, чтобы при повторной установке не появлялись копии.
	/// </summary>
	private async Task OpenGamePortAsync(int port)
	{
		var text = port.ToString(CultureInfo.InvariantCulture);
		foreach (var protocol in new[] { "TCP", "UDP" })
		{
			var name = $"Vintage Story {text} {protocol}";
			await Shell.RunAsync("netsh.exe", ["advfirewall", "firewall", "delete", "rule", $"name={name}"]).ConfigureAwait(false);
			var added = await Shell.RunAsync("netsh.exe", ["advfirewall", "firewall", "add", "rule", $"name={name}", "dir=in", "action=allow", $"protocol={protocol}", $"localport={text}"]).ConfigureAwait(false);
			if (!added.Succeeded)
			{
				this.host.Warn($"порт {text}/{protocol} в брандмауэре не открыт: {added.Reason}");
				return;
			}
		}

		Output.WriteLine($"В брандмауэре Windows открыт игровой порт {text} (TCP и UDP).");
	}

	/// <summary>
	/// Среда .NET для игры через официальный <c>dotnet-install.ps1</c> в <c>Program Files\dotnet</c>,
	/// где её ищет демон. Панели и демону она не нужна: они self-contained.
	/// </summary>
	private async Task<bool> InstallDotnetAsync(string channel)
	{
		var major = channel.Split('.')[0];
		var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
		var binary = Path.Combine(target, "dotnet.exe");
		if (File.Exists(binary)
			&& (await Shell.RunAsync(binary, ["--list-runtimes"]).ConfigureAwait(false)).Output
				.Split('\n').Any(line => line.StartsWith($"Microsoft.NETCore.App {major}.", StringComparison.Ordinal)))
		{
			Output.WriteLine($"Среда .NET {channel} уже установлена: {binary}");
			return true;
		}

		var script = Path.Combine(Path.GetTempPath(), $"dotnet-install-{Guid.NewGuid():N}.ps1");
		try
		{
			using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) })
			{
				await File.WriteAllBytesAsync(script, await client.GetByteArrayAsync(new Uri("https://dot.net/v1/dotnet-install.ps1")).ConfigureAwait(false)).ConfigureAwait(false);
			}

			var installed = await Shell.RunAsync(
					"powershell.exe",
					["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Runtime", "dotnet", "-Channel", channel, "-InstallDir", target, "-NoPath"],
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
			this.host.Warn($"не удалось скачать dotnet-install.ps1 ({exception.Message}); установите среду .NET {channel} вручную: https://dotnet.microsoft.com/download");
			return false;
		}
		finally
		{
			File.Delete(script);
		}

		Output.WriteLine($"Установлена среда .NET {channel} в {target}");
		return true;
	}

	/// <summary>Предупреждение о длинных путях: пути миров легко превышают 260 символов.</summary>
	private void WarnLongPaths()
	{
		using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
		if (key?.GetValue("LongPathsEnabled") is not 1)
		{
			this.host.Warn("длинные пути (больше 260 символов) в системе выключены. Включить (PowerShell от администратора):\n"
				+ "  New-ItemProperty -Path HKLM:\\SYSTEM\\CurrentControlSet\\Control\\FileSystem -Name LongPathsEnabled -Value 1 -PropertyType DWord -Force");
		}
	}

	/// <summary>Ярлыки на страницу панели в меню «Пуск» и на общем рабочем столе.</summary>
	private void WriteShortcuts()
	{
		var content = Paths.ShortcutContent(PanelState.LocalAddress(this.host.PanelEnvironment()));
		foreach (var folder in ShortcutFolders())
		{
			try
			{
				File.WriteAllText(Path.Combine(folder, ShortcutName), content);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				this.host.Warn($"ярлык в {folder} не создан: {exception.Message}");
			}
		}
	}

	public const string ShortcutName = "Vintage Story Panel.url";

	public static IEnumerable<string> ShortcutFolders() =>
		new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.CommonDesktopDirectory }
			.Select(Environment.GetFolderPath)
			.Where(folder => folder.Length > 0 && Directory.Exists(folder));

	private void PrintSummary(int serverPort)
	{
		var address = PanelState.LocalAddress(this.host.PanelEnvironment());
		Output.WriteLine($"\nПанель установлена: {address}");
		Output.WriteLine("Ярлык «Vintage Story Panel» — в меню «Пуск» и на рабочем столе.");
		if (serverPort > 0)
		{
			Output.WriteLine($"Игровой сервер main создан и запущен, порт {serverPort.ToString(CultureInfo.InvariantCulture)}.");
		}

		Output.WriteLine("Управление: vs-panel help (в командной строке администратора)");
	}

	/// <summary>Запись во временный файл и переименование: читатель видит либо старое, либо новое содержимое.</summary>
	private static void WriteFile(string path, string content)
	{
		var temporary = path + ".new";
		File.WriteAllText(temporary, content, new System.Text.UTF8Encoding(false));
		File.Move(temporary, path, overwrite: true);
	}

	private static void DeleteDirectory(string path)
	{
		if (Directory.Exists(path))
		{
			Directory.Delete(path, recursive: true);
		}
	}

	private static string ReadOrEmpty(string path) => File.Exists(path) ? File.ReadAllText(path) : string.Empty;

	private static string? ReadOrNull(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

	private bool Report(string message, bool value)
	{
		Output.WriteLine(message);
		return value;
	}
}
