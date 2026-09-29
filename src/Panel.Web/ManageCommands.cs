using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Panel.Access.Domain;
using Panel.Access.Infrastructure;
using Panel.Access.Passwords;
using Panel.Access.Sessions;
using Panel.Catalogues.Infrastructure;
using Panel.Contracts;
using Panel.Fleet.Ports;
using Panel.Provisioning.Domain;
using Panel.Provisioning.Ports;
using Panel.Runtime.Domain;
using Panel.Runtime.Ports;
using Panel.SharedKernel;
using Panel.Web.Application;
using Panel.Web.Endpoints;

namespace Panel.Web;

/// <summary>
/// <c>vs-panel-web manage &lt;команда&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// Установщик (<c>vs-panel install</c>) вызывает эти команды от имени служебной учётной записи
/// панели. Форматы состояния знают эти команды, а не установщик: установщик отвечает за
/// файлы, юниты и пользователей, эти команды — за содержимое <c>PANEL_STATE_DIR</c>.
/// </para>
/// <para>
/// При отказе текст пишется в stderr и возвращается код 1.
/// </para>
/// <para>
/// Вывод — по-английски, как у всего установщика: фразы отказов и записей операции переводятся
/// тем же каталогом, что и ответы панели (<see cref="Terminal"/>).
/// </para>
/// </remarks>
public static class ManageCommands
{
	/// <summary>
	/// Файлы устаревшей раскладки, которые лежали прямо в каталоге состояния и принадлежат одному
	/// серверу; они переносятся в <c>instances/&lt;id&gt;/</c>. Учётные записи, ключ сессий и аудит
	/// общие для панели и остаются на месте.
	/// </summary>
	public static IReadOnlyList<string> InstanceStateFiles { get; } =
	[
		"metrics.jsonl", "telemetry.jsonl", "automation.json", "automation-runs.jsonl",
		"events.jsonl", "updates.json", "update-runs.jsonl", "config-apply.json",
	];

	/// <summary>Как часто <c>provision</c> смотрит в запись операции.</summary>
	private static readonly TimeSpan progressPoll = TimeSpan.FromSeconds(2);

	public const string Usage = """
		Usage: vs-panel-web manage <command>
		  init             state directories, session key, migration of the legacy state layout
		  claim-token      one-time link to the setup wizard
		  set-password     new password for the admin account (read from stdin)
		  rotate-secret    replace the session key; all sessions end
		  provision [--version V] [--id main] [--name N] [--port 42420] [--world W]
		            [--language ru] [--wait 600]
		                   create the first server with the wizard defaults
		  servers-root <path>
		                   directory where new servers are created
		""";

	public static async Task<int> RunAsync(IReadOnlyList<string> arguments, PanelOptions options, TextWriter output, TextWriter error)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(output);
		ArgumentNullException.ThrowIfNull(error);

		var command = arguments.Count == 0 ? string.Empty : arguments[0];
		Result<Done> result;
		switch (command)
		{
			case "init":
				result = Initialise(options, output);
				break;
			case "claim-token":
				result = await IssueClaimTokenAsync(options, output, CancellationToken.None).ConfigureAwait(false);
				break;
			case "set-password":
				result = await SetPasswordAsync(options, ReadPassword(output), output, CancellationToken.None).ConfigureAwait(false);
				break;
			case "rotate-secret":
				result = RotateSecret(options, output);
				break;
			case "provision":
				result = await ProvisionAsync([.. arguments.Skip(1)], options, output, CancellationToken.None).ConfigureAwait(false);
				break;
			case "servers-root" when arguments.Count == 2:
				result = await SetServersRootAsync(arguments[1], options, output, CancellationToken.None).ConfigureAwait(false);
				break;
			default:
				await error.WriteLineAsync(Usage).ConfigureAwait(false);
				return 2;
		}

		if (!result.IsSuccess)
		{
			await error.WriteLineAsync(Terminal(result.Failure!.Text)).ConfigureAwait(false);
			return 1;
		}

		return 0;
	}

	/// <summary>
	/// Каталоги состояния и staging (0700), перенос устаревшей раскладки и ключ сессий, если его
	/// ещё нет. Повторный вызов ничего не меняет.
	/// </summary>
	public static Result<Done> Initialise(PanelOptions options, TextWriter output)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(output);
		try
		{
			CreatePrivateDirectory(options.StateDirectory);
			CreatePrivateDirectory(options.StagingDirectory);
			MigrateStateLayout(options, output);
			if (!File.Exists(options.SecretKeyPath))
			{
				WritePrivate(options.SecretKeyPath, RandomNumberGenerator.GetBytes(48), FileMode.CreateNew);
			}

			return Done.Ok;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Каталог состояния {options.StateDirectory} не подготовлен: {exception.Message}");
		}
	}

	/// <summary>
	/// Файлы устаревшей раскладки получает только сервер <c>main</c>, второй сервер не должен их
	/// забрать. Существующий файл назначения не заменяется.
	/// </summary>
	private static void MigrateStateLayout(PanelOptions options, TextWriter output)
	{
		if (options.DefaultInstance != "main" || InstanceId.Create("main") is not { IsSuccess: true } main)
		{
			return;
		}

		var target = options.InstanceDirectory(main.Value);
		CreatePrivateDirectory(target);
		var moved = new List<string>();
		foreach (var name in InstanceStateFiles)
		{
			var source = new FileInfo(Path.Combine(options.StateDirectory, name));
			var destination = Path.Combine(target, name);
			if (source.Exists && source.LinkTarget is null && !File.Exists(destination) && !Directory.Exists(destination))
			{
				File.Move(source.FullName, destination);
				moved.Add(name);
			}
		}

		if (moved.Count > 0)
		{
			output.WriteLine($"Server state moved to {target}: {string.Join(", ", moved)}");
		}
	}

	/// <summary>
	/// Новая ссылка отменяет прежнюю, поэтому ссылку, попавшую в историю оболочки, можно
	/// отменить, запросив новую.
	/// </summary>
	public static async Task<Result<Done>> IssueClaimTokenAsync(PanelOptions options, TextWriter output, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(output);
		var initialised = Initialise(options, output);
		if (!initialised.IsSuccess)
		{
			return initialised;
		}

		if (ReadJourney(options).IsCompleted)
		{
			return Failure.Conflict("Мастер установки уже завершён. Войдите в панель обычным способом.");
		}

		var accounts = await new FileAccountStore(options.AccountsPath, options.LegacyPasswordPath).LoadAsync(ct).ConfigureAwait(false);
		if (!accounts.IsSuccess)
		{
			return accounts.Failure!;
		}

		if (!accounts.Value.IsEmpty)
		{
			return Failure.Conflict("Администратор уже создан. Смените пароль: sudo vs-panel password");
		}

		// Как secrets.token_urlsafe(24): 24 байта в base64url без выравнивания, 32 символа.
		var token = Base64Url(RandomNumberGenerator.GetBytes(24));
		try
		{
			WritePrivate(options.SetupTokenPath, System.Text.Encoding.UTF8.GetBytes(token + "\n"), FileMode.Create);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Токен мастера не записан: {exception.Message}");
		}

		output.WriteLine("Open the setup wizard to finish configuration:");
		output.WriteLine($"  {SetupLink(options, token)}");
		var network = NetworkSetupLinks(options, token, MachineAddresses());
		if (network.Count > 0)
		{
			output.WriteLine("From another computer on the network:");
			foreach (var link in network)
			{
				output.WriteLine($"  {link}");
			}
		}

		output.WriteLine("The link works once: the token is removed after the administrator is created.");
		return Done.Ok;
	}

	/// <summary>
	/// Адрес мастера на этой машине. Если панель слушает все адреса, используется 127.0.0.1:
	/// эту ссылку установщик Windows открывает в браузере, поэтому она печатается первой.
	/// </summary>
	public static string SetupLink(PanelOptions options, string token)
	{
		ArgumentNullException.ThrowIfNull(options);
		var host = options.Bind is "0.0.0.0" or "::" ? "127.0.0.1" : options.Bind;
		return $"http://{host}:{options.Port.ToString(CultureInfo.InvariantCulture)}/setup?token={token}";
	}

	/// <summary>Адреса мастера с других машин: по адресу каждого интерфейса, если панель видна снаружи.</summary>
	public static IReadOnlyList<string> NetworkSetupLinks(PanelOptions options, string token, IEnumerable<System.Net.IPAddress> machine)
	{
		ArgumentNullException.ThrowIfNull(options);
		return
		[
			.. PanelListener.Create(options.Bind, options.Port, FirewallScope.Lan)
				.NetworkUrls(machine)
				.Select(url => $"{url}setup?token={token}"),
		];
	}

	private static IReadOnlyList<System.Net.IPAddress> MachineAddresses()
	{
		try
		{
			return
			[
				.. System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
					.Where(nic => nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
					.SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
					.Select(unicast => unicast.Address),
			];
		}
		catch (System.Net.NetworkInformation.NetworkInformationException)
		{
			return [];
		}
	}

	/// <summary>
	/// Пароль учётной записи admin; если её нет, она создаётся с ролью администратора. Поколение
	/// растёт, и прежние сессии admin перестают действовать.
	/// </summary>
	public static async Task<Result<Done>> SetPasswordAsync(PanelOptions options, (string First, string Second) typed, TextWriter output, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(output);
		var initialised = Initialise(options, output);
		if (!initialised.IsSuccess)
		{
			return initialised;
		}

		if (typed.First != typed.Second)
		{
			return Failure.Invalid("Пароли не совпадают");
		}

		var password = PasswordHash.Create(typed.First);
		if (!password.IsSuccess)
		{
			return password.Failure!;
		}

		var store = new FileAccountStore(options.AccountsPath, options.LegacyPasswordPath);
		var directory = await store.LoadAsync(ct).ConfigureAwait(false);
		if (!directory.IsSuccess)
		{
			return directory.Failure!;
		}

		var updated = directory.Value.Put(UserName.Administrator, password.Value, Role.Admin);
		if (!updated.IsSuccess)
		{
			return updated.Failure!;
		}

		var saved = await store.SaveAsync(updated.Value, ct).ConfigureAwait(false);
		if (!saved.IsSuccess)
		{
			return saved;
		}

		RestrictToOwner(options.AccountsPath);
		output.WriteLine("Administrator password saved");
		return Done.Ok;
	}

	/// <summary>Новый ключ сессий пишется рядом и заменяет старый одним переименованием.</summary>
	public static Result<Done> RotateSecret(PanelOptions options, TextWriter output)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(output);
		var initialised = Initialise(options, output);
		if (!initialised.IsSuccess)
		{
			return initialised;
		}

		var temporary = Path.ChangeExtension(options.SecretKeyPath, ".new");
		try
		{
			WritePrivate(temporary, RandomNumberGenerator.GetBytes(48), FileMode.Create);
			File.Move(temporary, options.SecretKeyPath, overwrite: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Ключ сессий не заменён: {exception.Message}");
		}

		output.WriteLine("Session key replaced; all active sessions ended");
		return Done.Ok;
	}

	/// <summary>Аргументы <c>provision</c> с теми же именами и умолчаниями, что у <c>app.manage</c>.</summary>
	public sealed record ProvisionArguments
	{
		public string Version { get; init; } = string.Empty;
		public string Id { get; init; } = "main";
		public string Name { get; init; } = string.Empty;
		public int Port { get; init; }
		public string World { get; init; } = string.Empty;
		public string Language { get; init; } = "ru";
		public int WaitSeconds { get; init; } = 600;

		public static Result<ProvisionArguments> Parse(IReadOnlyList<string> arguments)
		{
			ArgumentNullException.ThrowIfNull(arguments);
			var parsed = new ProvisionArguments();
			for (var index = 0; index < arguments.Count; index++)
			{
				var key = arguments[index];
				if (index + 1 >= arguments.Count)
				{
					return Failure.Invalid($"Нет значения у {key}");
				}

				var value = arguments[++index];
				switch (key)
				{
					case "--version":
						parsed = parsed with { Version = value };
						break;
					case "--id":
						parsed = parsed with { Id = value };
						break;
					case "--name":
						parsed = parsed with { Name = value };
						break;
					case "--world":
						parsed = parsed with { World = value };
						break;
					case "--language":
						parsed = parsed with { Language = value };
						break;
					case "--port" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port):
						parsed = parsed with { Port = port };
						break;
					case "--wait" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var wait) && wait > 0:
						parsed = parsed with { WaitSeconds = wait };
						break;
					default:
						return Failure.Invalid($"Неизвестный или неверный параметр: {key} {value}");
				}
			}

			return parsed;
		}

		/// <summary>Ответы мастера: умолчания <c>ServerBody</c> и поверх них заданные значения.</summary>
		public ServerAnswers ToAnswers(string version) => ServerRequest.WizardDefaults with
		{
			Id = Id,
			Name = Name,
			Version = version,
			Port = Port == 0 ? ServerSpec.DefaultPort : Port,
			WorldName = World.Length == 0 ? ServerSpec.DefaultWorldName : World,
			Language = Language,
		};
	}

	/// <summary>
	/// <c>servers-root</c>: каталог новых серверов, заданный установщиком до создания первого
	/// сервера. Путь проверяет и готовит демон, как при смене в настройках панели.
	/// </summary>
	private static async Task<Result<Done>> SetServersRootAsync(string path, PanelOptions options, TextWriter output, CancellationToken ct)
	{
		var secret = SessionSecret.Load(options.SecretKeyPath);
		if (!secret.IsSuccess)
		{
			return secret.Failure!;
		}

		var services = new ServiceCollection()
			.AddLogging(logging => logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning))
			.AddPanel(options, secret.Value);
		await using var provider = services.BuildServiceProvider();

		var changed = await provider.GetRequiredService<IProvisioningHost>().SetInstancesRootAsync(path.Trim(), ct).ConfigureAwait(false);
		if (!changed.IsSuccess)
		{
			return changed.Failure!;
		}

		output.WriteLine($"Servers directory: {changed.Value.InstancesRoot}");
		return Done.Ok;
	}

	/// <summary>
	/// <c>provision</c>: первый сервер из установщика через тот же <see cref="ProvisionService"/>,
	/// что у мастера. Выполняется до запуска службы панели, поэтому аренду обслуживания никто не
	/// держит. Мастер получает <c>prepared</c> и закрывается сам, когда по ссылке из установщика
	/// появится администратор.
	/// </summary>
	private static async Task<Result<Done>> ProvisionAsync(IReadOnlyList<string> arguments, PanelOptions options, TextWriter output, CancellationToken ct)
	{
		var parsed = ProvisionArguments.Parse(arguments);
		if (!parsed.IsSuccess)
		{
			return parsed.Failure!;
		}

		var initialised = Initialise(options, output);
		if (!initialised.IsSuccess)
		{
			return initialised;
		}

		if (ReadJourney(options).IsCompleted)
		{
			return Failure.Conflict("Мастер установки уже завершён: сервер создаётся в панели.");
		}

		var secret = SessionSecret.Load(options.SecretKeyPath);
		if (!secret.IsSuccess)
		{
			return secret.Failure!;
		}

		var services = new ServiceCollection()
			.AddLogging(logging => logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning))
			.AddPanel(options, secret.Value);
		await using var provider = services.BuildServiceProvider();

		var catalogue = provider.GetRequiredService<VersionCatalogue>();
		var version = parsed.Value.Version;
		if (version.Length == 0)
		{
			var snapshot = await catalogue.SnapshotAsync(refresh: true, ct).ConfigureAwait(false);
			if (!snapshot.IsSuccess)
			{
				return snapshot.Failure!;
			}

			version = snapshot.Value.LatestStable?.Version.Text ?? string.Empty;
			if (version.Length == 0)
			{
				return Failure.Unavailable("В каталоге нет стабильной версии Vintage Story");
			}
		}

		var spec = ServerSpec.FromWizard(parsed.Value.ToAnswers(version));
		if (!spec.IsSuccess)
		{
			return spec.Failure!;
		}

		var provision = new ProvisionService(
			provider.GetRequiredService<IProvisioningHost>(),
			provider.GetRequiredService<IRegistryAdministration>(),
			catalogue,
			provider.GetRequiredService<PackageDownloader>(),
			provider.GetRequiredService<StagingWriter>(),
			provider.GetRequiredService<MaintenanceCoordinator>(),
			provider.GetRequiredService<IServerRuntime>(),
			provider.GetRequiredService<ITelemetryReader>(),
			provider.GetRequiredService<ReadinessPolicy>(),
			provider.GetRequiredService<EventBus>(),
			options,
			new PanelInstance(options),
			provider.GetRequiredService<TimeProvider>(),
			provider.GetRequiredService<ILogger<ProvisionService>>())
		{
			ReadinessTimeout = TimeSpan.FromSeconds(parsed.Value.WaitSeconds),
		};

		var report = await provision.PreflightAsync(ChosenVersion.Create(version), ct).ConfigureAwait(false);
		foreach (var warning in Texts(report["warnings"]))
		{
			output.WriteLine("Warning: " + warning);
		}

		if (report["ok"] is not JsonValue ok || !ok.GetValue<bool>())
		{
			return Failure.Unavailable($"Сервер не создан:\n  {string.Join("\n  ", Texts(report["blockers"]))}");
		}

		output.WriteLine($"Creating server {spec.Value.Id.Value}: Vintage Story {version}, port {spec.Value.Port.ToString(CultureInfo.InvariantCulture)}");
		output.WriteLine("Downloading the package and the first start take a few minutes; progress follows, a line every 15 s.");
		output.Flush();
		var clock = provider.GetRequiredService<TimeProvider>();
		var progress = new ProvisionProgress(output, clock.GetUtcNow(), () => StagedBytes(options.StagingDirectory));
		var launched = await provision.LaunchAsync(spec.Value).ConfigureAwait(false);
		if (!launched.IsSuccess)
		{
			return launched.Failure!;
		}

		// StopAsync ждёт завершения идущего создания. Пока он ждёт, выводится ход по записи
		// операции, той же, что видит мастер.
		var finished = provision.StopAsync(CancellationToken.None);
		while (!finished.IsCompleted)
		{
			await Task.WhenAny(finished, Task.Delay(progressPoll, ct)).ConfigureAwait(false);
			progress.Report((await provision.SnapshotAsync(ct).ConfigureAwait(false))["current"] as JsonObject, clock.GetUtcNow());
		}

		await finished.ConfigureAwait(false);
		var history = (await provision.SnapshotAsync(ct).ConfigureAwait(false))["history"] as JsonArray ?? [];
		var record = history.OfType<JsonObject>().FirstOrDefault(item => Text(item["id"]) == launched.Value.ToString());
		progress.Report(record, clock.GetUtcNow());

		if (record is null || Text(record["status"]) != "success")
		{
			var detail = record is null ? string.Empty : Text(record["detail"]);
			return detail.Length > 0 ? Failure.Unavailable($"Сервер не создан: {detail}") : Failure.Unavailable("Сервер не создан: неизвестная ошибка");
		}

		output.WriteLine(Text(record["detail"]) is { Length: > 0 } done ? done : "Server created");

		// Служба панели читает окружение при запуске, поэтому сервер и его версия должны быть
		// записаны до того, как установщик её запустит.
		var host = provider.GetRequiredService<IProvisioningHost>();
		foreach (var (key, value) in new[] { ("PANEL_INSTANCE", spec.Value.Id.Value), ("PANEL_GAME_VERSION", version) })
		{
			var setting = PanelSetting.Create(key, value);
			var written = setting.IsSuccess
				? (await host.SetPanelEnvironmentAsync(setting.Value, ct).ConfigureAwait(false)).Failure
				: setting.Failure;
			if (written is not null)
			{
				output.WriteLine("Warning: panel environment not updated: " + Terminal(written.Text));
			}
		}

		var setup = provider.GetRequiredService<SetupService>();
		var prepared = await setup.ChangeAsync(journey => journey.PrepareByInstaller(spec.Value), ct).ConfigureAwait(false);
		return prepared.IsSuccess ? Done.Ok : prepared.Failure!;
	}

	/// <summary>Пароль дважды: из терминала без эха, из перенаправленного stdin построчно.</summary>
	private static (string First, string Second) ReadPassword(TextWriter output)
	{
		return (Ask("New administrator password (at least 12 characters): "), Ask("Repeat the password: "));

		string Ask(string prompt)
		{
			output.Write(prompt);
			if (Console.IsInputRedirected)
			{
				return Console.In.ReadLine() ?? string.Empty;
			}

			var typed = new System.Text.StringBuilder();
			while (true)
			{
				var key = Console.ReadKey(intercept: true);
				if (key.Key == ConsoleKey.Enter)
				{
					output.WriteLine();
					return typed.ToString();
				}

				if (key.Key == ConsoleKey.Backspace)
				{
					if (typed.Length > 0)
					{
						typed.Length--;
					}
				}
				else if (!char.IsControl(key.KeyChar))
				{
					typed.Append(key.KeyChar);
				}
			}
		}
	}

	private static SetupJourney ReadJourney(PanelOptions options)
	{
		try
		{
			return SetupJourney.FromJson(JsonNode.Parse(File.ReadAllText(options.SetupStatePath)));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return SetupJourney.FromJson(null);
		}
	}

	private static void CreatePrivateDirectory(string path)
	{
		if (OperatingSystem.IsWindows())
		{
			Directory.CreateDirectory(path);
		}
		else
		{
			Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
	}

	/// <summary>Файл только для владельца; права задаются при создании, а не после.</summary>
	private static void WritePrivate(string path, byte[] content, FileMode mode)
	{
		var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.None };
		if (!OperatingSystem.IsWindows())
		{
			options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		}

		using (var stream = new FileStream(path, options))
		{
			stream.Write(content);
		}

		RestrictToOwner(path);
	}

	/// <summary>Существующий файл мог быть создан с другими правами; они сужаются до 0600.</summary>
	private static void RestrictToOwner(string path)
	{
		if (!OperatingSystem.IsWindows() && File.Exists(path))
		{
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}
	}

	private static string Base64Url(byte[] bytes) =>
		Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

	private static IEnumerable<string> Texts(JsonNode? node) =>
		(node as JsonArray ?? []).Select(Text).Where(text => text.Length > 0);

	/// <summary>Строка или фраза (<c>$phrase</c>) из записи операции — по-английски, как весь вывод.</summary>
	private static string Text(JsonNode? node) =>
		PhraseJsonConverter.FromNode(node) is { } phrase ? Terminal(phrase) : string.Empty;

	/// <summary>Фраза для терминала: установщик говорит по-английски.</summary>
	internal static string Terminal(Phrase phrase) => PhraseCatalog.Translate(phrase, PanelLanguage.English);

	/// <summary>
	/// Сколько уже скачано: размер самого нового файла в staging. Имя пакета снаружи
	/// <see cref="ProvisionService"/> не видно, а других загрузок при остановленной панели нет.
	/// </summary>
	private static long? StagedBytes(string directory)
	{
		try
		{
			var info = new DirectoryInfo(directory);
			return info.Exists ? info.GetFiles().OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault()?.Length : null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}
}
