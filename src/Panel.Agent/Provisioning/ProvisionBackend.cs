using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Agent.Content;
using Panel.Content.Domain;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.Fleet.Infrastructure;
using Panel.Fleet.Ports;
using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Provisioning;

/// <summary>
/// Создание сервера и дела самой панели на привилегированной стороне: <c>instance-create</c>,
/// <c>instance-service-install</c>, <c>runtime-probe</c>, <c>disk-free</c>,
/// <c>panel-env-set</c>, <c>panel-restart</c>.
/// </summary>
/// <remarks>
/// <para>
/// Создание — одна привилегированная команда, все пути выбирает демон (INV-43): панель
/// присылает описание сервера с закрытым набором ключей и имя пакета в staging. Сбой во время
/// создания удаляет созданное дерево; после записи в реестр ничего не удаляется (INV-44).
/// </para>
/// <para>
/// Порядок шагов общий для обеих платформ. Как игра получает файлы, от чьего имени
/// запускается <c>--genconfig</c> и что значит «поставить службу», решает
/// <see cref="IProvisioningPlatform"/>: <see cref="SystemdProvisioning"/> на Linux,
/// <see cref="SupervisedProvisioning"/> на Windows.
/// </para>
/// </remarks>
public sealed class ProvisionBackend
{
	private static readonly UnixFileMode mode755 =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
		| UnixFileMode.GroupRead | UnixFileMode.GroupExecute
		| UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

	private static readonly UnixFileMode mode750 =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
		| UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

	private static readonly UnixFileMode mode700 =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
	private static readonly UnixFileMode mode600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

	private static readonly UnixFileMode mode644 =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

	private static readonly TimeSpan genconfigTimeout = TimeSpan.FromSeconds(300);

	private static readonly UnixFileMode mode640 =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

	private readonly string panelConfigPath;
	private readonly IRegistryStore registry;
	private readonly RegistryEditor editor;
	private readonly StagingArea staging;
	private readonly IProvisioningPlatform platform;

	public ProvisionBackend(
		string panelConfigPath,
		IRegistryStore registry,
		RegistryEditor editor,
		StagingArea staging,
		IProvisioningPlatform platform)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(panelConfigPath);
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(editor);
		ArgumentNullException.ThrowIfNull(staging);
		ArgumentNullException.ThrowIfNull(platform);
		this.panelConfigPath = panelConfigPath;
		this.registry = registry;
		this.editor = editor;
		this.staging = staging;
		this.platform = platform;
	}

	/// <summary>Проверить привязанный к длине и SHA256 переносимый пакет без записи его содержимого.</summary>
	public async Task<Result<TransferInspectionPayload>> InspectTransferAsync(string name, string sha256, string length, CancellationToken ct)
	{
		var source = await TransferSourceAsync(name, sha256, length, ct).ConfigureAwait(false);
		if (!source.IsSuccess)
		{
			return source.Failure!;
		}

		var configuration = await ConfigurationAsync(ct).ConfigureAwait(false);
		if (!configuration.IsSuccess)
		{
			return configuration.Failure!;
		}

		var snapshot = Path.Combine(configuration.Value.InstancesRoot, ".vs-transfer-" + Guid.NewGuid().ToString("N"));
		try
		{
			var secured = await SecureRootAsync(configuration.Value, ct).ConfigureAwait(false);
			if (!secured.IsSuccess)
			{
				return secured.Failure!;
			}

			var copied = await SnapshotTransferAsync(source.Value, snapshot, sha256, length, ct).ConfigureAwait(false);
			if (!copied.IsSuccess)
			{
				return copied.Failure!;
			}

			var identity = await TreeArchive.InspectTransferAsync(snapshot, ct).ConfigureAwait(false);
			return identity.IsSuccess
				? new TransferInspectionPayload
				{
					ActiveWorld = identity.Value.ActiveWorld,
					GameVersion = identity.Value.GameVersion,
					ManifestSha256 = identity.Value.ManifestSha256,
				}
				: Result.Fail<TransferInspectionPayload>(identity.Failure!);
		}
		finally
		{
			TryDeleteFile(snapshot);
		}
	}

	/// <summary>Создать новый сервер из официальной программы и отдельно проверенных переносимых данных.</summary>
	public async Task<Result<InstanceCreatedPayload>> ImportAsync(
		JsonNode? specification, string transferName, string sha256, string length, CancellationToken ct)
	{
		var transfer = await TransferSourceAsync(transferName, sha256, length, ct).ConfigureAwait(false);
		if (!transfer.IsSuccess)
		{
			return transfer.Failure!;
		}

		var parsed = InstanceSpecification.Parse(specification);
		if (!parsed.IsSuccess)
		{
			return parsed.Failure!;
		}

		var (spec, packageName) = parsed.Value;
		var configuration = await ConfigurationAsync(ct).ConfigureAwait(false);
		if (!configuration.IsSuccess)
		{
			return configuration.Failure!;
		}

		if (configuration.Value.IsReadOnlyRegistry)
		{
			return Failure.Conflict("Для создания сервера нужен реестр версии 2");
		}

		var registered = await registry.LoadAsync(ct).ConfigureAwait(false);
		if (registered is not RegistrySnapshot.Available snapshot)
		{
			return Failure.Unavailable(((RegistrySnapshot.Unavailable) registered).Detail);
		}

		var clash = Clash(snapshot.Servers.Select(server => (server.Id.Value, PortOf(server.Raw))), spec);
		if (clash is not null)
		{
			return clash;
		}

		var layout = Layout(configuration.Value, spec.Id);
		if (!layout.IsSuccess)
		{
			return layout.Failure!;
		}

		var paths = layout.Value;
		if (Path.Exists(paths.Root))
		{
			return Failure.Conflict($"Directory {paths.Root} already exists");
		}

		var staged = StagedFile.Create(packageName);
		if (!staged.IsSuccess || !staged.Value.Name.StartsWith(StagedFile.Purpose.Game, StringComparison.Ordinal))
		{
			return Failure.Invalid("Временный файл не является пакетом игры");
		}

		if (PackageArchive.ExtensionOf(staged.Value.Name) is not { } extension)
		{
			return Failure.Invalid("Пакет игры должен быть архивом .tar.gz или .zip");
		}

		var package = await staging.ResolveAsync(staged.Value, extension, ct).ConfigureAwait(false);
		if (!package.IsSuccess)
		{
			return package.Failure!;
		}

		var entries = await PackageArchive.ListAsync(package.Value, ct).ConfigureAwait(false);
		var details = entries.IsSuccess ? ServerPackage.Judge(entries.Value) : Result.Fail<PackageLayout>(entries.Failure!);
		if (!details.IsSuccess)
		{
			return details.Failure!;
		}

		var prepared = await platform.PrepareAsync(paths, ct).ConfigureAwait(false);
		if (!prepared.IsSuccess)
		{
			return prepared.Failure!;
		}

		var secured = await SecureRootAsync(configuration.Value, ct).ConfigureAwait(false);
		if (!secured.IsSuccess)
		{
			return secured.Failure!;
		}

		var archive = Path.Combine(paths.InstancesRoot, ".vs-transfer-" + Guid.NewGuid().ToString("N"));
		var reservation = Path.Combine(paths.InstancesRoot, ".vs-import-" + spec.Id.Value);
		var created = false;
		var committed = false;
		var registrationAccepted = false;
		var reservedByUs = false;
		FileStream? reserved = null;
		try
		{
			reserved = new FileStream(reservation, FileMode.CreateNew, FileAccess.Write, FileShare.None);
			reservedByUs = true;
			if (Path.Exists(paths.Root))
			{
				return Failure.Conflict($"Directory {paths.Root} already exists");
			}

			// Хеш и длина сверяются при копировании: дальше работаем только с закрытой от других копией.
			var copied = await SnapshotTransferAsync(transfer.Value, archive, sha256, length, ct).ConfigureAwait(false);
			if (!copied.IsSuccess)
			{
				return copied.Failure!;
			}

			Directory.CreateDirectory(paths.Root);
			created = true;
			GameFiles.SetMode(paths.Root, mode750);
			foreach (var directory in paths.SkeletonDirectories)
			{
				Directory.CreateDirectory(directory);
				GameFiles.SetMode(directory, mode750);
			}

			var extracted = await ExtractProgramAsync(package.Value, details.Value, paths, ct).ConfigureAwait(false);
			if (!extracted.IsSuccess)
			{
				return extracted.Failure!;
			}

			var owned = await platform.GiveToGameAsync(paths.Root, ct).ConfigureAwait(false);
			if (!owned.IsSuccess)
			{
				return owned.Failure!;
			}

			var configured = await GenerateConfigAsync(platform, prepared.Value, spec, paths, ct).ConfigureAwait(false);
			if (!configured.IsSuccess)
			{
				return configured.Failure!;
			}

			// ExtractTransferAsync судит весь архив заново прямо перед записью и пишет в новый каталог.
			var imported = Path.Combine(paths.Root, ".transfer-data");
			var unpacked = await TreeArchive.ExtractTransferAsync(archive, imported, ct).ConfigureAwait(false);
			if (!unpacked.IsSuccess)
			{
				return unpacked.Failure!;
			}

			var applied = await ApplyImportedAsync(imported, spec, paths, ct).ConfigureAwait(false);
			if (!applied.IsSuccess)
			{
				return applied.Failure!;
			}

			TryDelete(imported);
			var telemetry = SeedTelemetryMod(configuration.Value.TelemetryDirectory, paths.ModsDirectory);
			owned = await platform.GiveToGameAsync(paths.Root, ct).ConfigureAwait(false);
			if (!owned.IsSuccess)
			{
				return owned.Failure!;
			}

			var written = await editor.EditAsync(document =>
			{
				var again = Clash(document.Instances.Select(pair => (pair.Key, PortOf(pair.Value as JsonObject))), spec);
				if (again is not null)
				{
					return Result.Fail<Done>(again);
				}

				registrationAccepted = true;
				document.Instances[spec.Id.Value] = paths.RegistryEntry(spec);
				document.Default ??= spec.Id;
				return Done.Ok;
			}, ct).ConfigureAwait(false);
			if (!written.IsSuccess)
			{
				return written.Failure!;
			}

			committed = true;
			return new InstanceCreatedPayload
			{
				Id = spec.Id.Value,
				Root = paths.Root,
				Unit = paths.Unit,
				Port = spec.Port,
				TelemetryMod = telemetry,
			};
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
		{
			return Failure.Unavailable(exception.Message);
		}
		finally
		{
			reserved?.Dispose();
			if (!committed && !registrationAccepted && created)
			{
				TryDelete(paths.Root);
			}

			TryDeleteFile(archive);
			if (reservedByUs)
			{
				TryDeleteFile(reservation);
			}
		}
	}

	/// <summary>Наложить переносимые настройки на сгенерированные и разложить мир и моды по раскладке сервера.</summary>
	private static async Task<Result<Done>> ApplyImportedAsync(string imported, ServerSpec spec, InstanceLayout paths, CancellationToken ct)
	{
		var portableFile = Path.Combine(imported, "data", "serverconfig.json");
		if (JsonNode.Parse(await File.ReadAllTextAsync(portableFile, ct).ConfigureAwait(false)) is not JsonObject portable)
		{
			return Failure.Invalid("Настройки пакета повреждены");
		}

		var rebased = ServerConfigPaths.FromLayout(paths).Rebase(portable);
		if (!rebased.IsSuccess)
		{
			return rebased.Failure!;
		}

		if (JsonNode.Parse(await File.ReadAllTextAsync(paths.ConfigFile, ct).ConfigureAwait(false)) is not JsonObject generated)
		{
			return Failure.Invalid("Настройки нового сервера повреждены");
		}

		foreach (var (key, value) in rebased.Value)
		{
			if (key == "WorldConfig" && value is JsonObject worldConfig && generated[key] is JsonObject generatedWorld)
			{
				foreach (var (field, setting) in worldConfig)
				{
					generatedWorld[field] = setting?.DeepClone();
				}
			}
			else
			{
				generated[key] = value?.DeepClone();
			}
		}

		var activeWorld = (string?) (rebased.Value["WorldConfig"] as JsonObject)?["SaveFileLocation"];
		if (activeWorld is null)
		{
			return Failure.Invalid("В пакете нет активного мира");
		}

		generated = InitialConfig.Apply(generated, spec, paths);
		((JsonObject) generated["WorldConfig"]!)["SaveFileLocation"] = activeWorld;
		await File.WriteAllTextAsync(paths.ConfigFile, generated.ToJsonString(configJson) + "\n", new UTF8Encoding(false), ct).ConfigureAwait(false);
		GameFiles.SetMode(paths.ConfigFile, mode640);
		foreach (var file in Directory.EnumerateFiles(Path.Combine(imported, "data", "Saves")))
		{
			File.Move(file, Path.Combine(paths.WorldDirectory, Path.GetFileName(file)));
		}

		if (Directory.Exists(Path.Combine(imported, "data", "Mods")))
		{
			foreach (var file in Directory.EnumerateFiles(Path.Combine(imported, "data", "Mods")))
			{
				File.Move(file, Path.Combine(paths.ModsDirectory, Path.GetFileName(file)), overwrite: true);
			}
		}

		return Done.Ok;
	}

	private async Task<Result<string>> TransferSourceAsync(string name, string sha256, string length, CancellationToken ct)
	{
		var staged = StagedFile.Create(name);
		if (!staged.IsSuccess || !staged.Value.Name.StartsWith(StagedFile.Purpose.Transfer, StringComparison.Ordinal)
			|| !staged.Value.Name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
			|| sha256.Length != 64 || !sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
			|| !long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size <= 0)
		{
			return Failure.Invalid("Неверное имя, SHA256 или размер пакета сервера");
		}

		return await staging.ResolveAsync(staged.Value, ".tar.gz", ct).ConfigureAwait(false);
	}

	private async Task<Result<Done>> SecureRootAsync(PanelConfiguration configuration, CancellationToken ct)
	{
		if (!Directory.Exists(configuration.InstancesRoot))
		{
			var root = Choose(configuration, configuration.InstancesRoot);
			if (!root.IsSuccess)
			{
				return root.Failure!;
			}

			var secured = await platform.SecureServersRootAsync(root.Value, owned: true, ct).ConfigureAwait(false);
			if (!secured.IsSuccess)
			{
				return secured.Failure!;
			}
		}

		Directory.CreateDirectory(configuration.InstancesRoot);
		GameFiles.SetMode(configuration.InstancesRoot, mode755);
		return Done.Ok;
	}

	private static async Task<Result<Done>> SnapshotTransferAsync(string source, string destination, string sha256, string length, CancellationToken ct)
	{
		var expected = long.Parse(length, CultureInfo.InvariantCulture);
		var info = new FileInfo(source);
		if (!info.Exists || info.LinkTarget is not null || info.Length != expected)
		{
			return Failure.Invalid("Пакет сервера изменился или является ссылкой");
		}

		await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
		if (new FileInfo(source).LinkTarget is not null || input.Length != expected)
		{
			return Failure.Invalid("Пакет сервера изменился или является ссылкой");
		}

		await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
		GameFiles.SetMode(destination, mode600);
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		var buffer = new byte[64 * 1024];
		long total = 0;
		int read;
		while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
		{
			if (read > expected - total)
			{
				return Failure.Invalid("Размер пакета сервера изменился");
			}

			total += read;
			hash.AppendData(buffer.AsSpan(0, read));
			await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
		}

		return total == expected && Convert.ToHexStringLower(hash.GetHashAndReset()) == sha256
			? Done.Ok
			: Failure.Invalid("SHA256 или размер пакета сервера не совпадает");
	}

	private static void TryDeleteFile(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
		}
	}

	public Task<RuntimeProbePayload> ProbeRuntimeAsync(CancellationToken ct) => platform.ProbeRuntimeAsync(ct);

	/// <summary>
	/// Свободное место там, где будут создаваться серверы: на ближайшем существующем предке
	/// <c>instances_root</c>.
	/// </summary>
	public async Task<Result<DiskFreePayload>> DiskFreeAsync(CancellationToken ct)
	{
		var configuration = await ConfigurationAsync(ct).ConfigureAwait(false);
		if (!configuration.IsSuccess)
		{
			return configuration.Failure!;
		}

		var root = configuration.Value.InstancesRoot;
		var target = new DirectoryInfo(root);
		while (!target.Exists && target.Parent is { } parent)
		{
			target = parent;
		}

		var usage = GameFiles.Usage(target.FullName);
		return new DiskFreePayload
		{
			Path = target.FullName,
			Total = usage.Total,
			Used = usage.Used,
			Free = usage.Free,
			InstancesRoot = root,
		};
	}

	/// <summary>Создать дерево сервера и записать сервер в реестр.</summary>
	public async Task<Result<InstanceCreatedPayload>> CreateAsync(JsonNode? specification, CancellationToken ct)
	{
		var parsed = InstanceSpecification.Parse(specification);
		if (!parsed.IsSuccess)
		{
			return parsed.Failure!;
		}

		var (spec, packageName) = parsed.Value;
		var configuration = await ConfigurationAsync(ct).ConfigureAwait(false);
		if (!configuration.IsSuccess)
		{
			return configuration.Failure!;
		}

		if (configuration.Value.IsReadOnlyRegistry)
		{
			return Failure.Conflict("Creating servers needs /etc/vs-panel.json version 2; run scripts/install.sh");
		}

		var snapshot = await registry.LoadAsync(ct).ConfigureAwait(false);
		if (snapshot is not RegistrySnapshot.Available registered)
		{
			return Failure.Unavailable(((RegistrySnapshot.Unavailable) snapshot).Detail);
		}

		var clash = Clash(registered.Servers.Select(server => (server.Id.Value, PortOf(server.Raw))), spec);
		if (clash is not null)
		{
			return clash;
		}

		var layout = Layout(configuration.Value, spec.Id);
		if (!layout.IsSuccess)
		{
			return layout.Failure!;
		}

		var paths = layout.Value;
		if (Path.Exists(paths.Root))
		{
			return Failure.Conflict($"Directory {paths.Root} already exists");
		}

		var staged = StagedFile.Create(packageName);
		if (!staged.IsSuccess || !staged.Value.Name.StartsWith(StagedFile.Purpose.Game, StringComparison.Ordinal))
		{
			return Failure.Invalid("Временный файл не является пакетом игры");
		}

		if (PackageArchive.ExtensionOf(staged.Value.Name) is not { } extension)
		{
			return Failure.Invalid("Пакет игры должен быть архивом .tar.gz или .zip");
		}

		var package = await staging.ResolveAsync(staged.Value, extension, ct).ConfigureAwait(false);
		if (!package.IsSuccess)
		{
			return package.Failure!;
		}

		var entries = await PackageArchive.ListAsync(package.Value, ct).ConfigureAwait(false);
		var details = entries.IsSuccess ? ServerPackage.Judge(entries.Value) : Result.Fail<PackageLayout>(entries.Failure!);
		if (!details.IsSuccess)
		{
			return details.Failure!;
		}

		// Всё, без чего --genconfig не запустится, проверяется до первой записи на диск.
		var prepared = await platform.PrepareAsync(paths, ct).ConfigureAwait(false);
		if (!prepared.IsSuccess)
		{
			return prepared.Failure!;
		}

		var dotnet = prepared.Value;

		// Каталога серверов ещё нет (первый сервер или каталог удалён). Он создаётся с правами
		// каталога серверов, а не с унаследованными: на Windows вне ProgramData обычный
		// пользователь получил бы права на дерево, которое запускает SYSTEM.
		if (!Directory.Exists(paths.InstancesRoot))
		{
			var root = Choose(configuration.Value, paths.InstancesRoot);
			var secured = root.IsSuccess
				? await platform.SecureServersRootAsync(root.Value, owned: true, ct).ConfigureAwait(false)
				: Result.Fail<Done>(root.Failure!);
			if (!secured.IsSuccess)
			{
				return secured.Failure!;
			}
		}

		var created = false;
		try
		{
			// Права выставляются явно, чтобы от umask демона не зависело, войдёт ли служба игры в
			// WorkingDirectory: корень без бита поиска ронял бы юнит на шаге CHDIR.
			Directory.CreateDirectory(paths.InstancesRoot);
			GameFiles.SetMode(paths.InstancesRoot, mode755);
			Directory.CreateDirectory(paths.Root);
			created = true;
			GameFiles.SetMode(paths.Root, mode750);
			foreach (var directory in paths.SkeletonDirectories)
			{
				Directory.CreateDirectory(directory);
				GameFiles.SetMode(directory, mode750);
			}

			var extracted = await ExtractProgramAsync(package.Value, details.Value, paths, ct).ConfigureAwait(false);
			if (!extracted.IsSuccess)
			{
				return Abandon(paths, extracted.Failure!);
			}

			// До --genconfig, потому что игра пишет конфигурацию от своего имени. Второй проход
			// подбирает то, что root добавил после.
			var owned = await platform.GiveToGameAsync(paths.Root, ct).ConfigureAwait(false);
			if (!owned.IsSuccess)
			{
				return Abandon(paths, owned.Failure!);
			}

			var configured = await GenerateConfigAsync(platform, dotnet, spec, paths, ct).ConfigureAwait(false);
			if (!configured.IsSuccess)
			{
				return Abandon(paths, configured.Failure!);
			}

			var telemetry = SeedTelemetryMod(configuration.Value.TelemetryDirectory, paths.ModsDirectory);
			owned = await platform.GiveToGameAsync(paths.Root, ct).ConfigureAwait(false);
			if (!owned.IsSuccess)
			{
				return Abandon(paths, owned.Failure!);
			}

			var written = await editor.EditAsync(document =>
			{
				// Проверка ещё раз, под блокировкой реестра: снимок выше мог устареть.
				var again = Clash(
					document.Instances.Select(pair => (pair.Key, PortOf(pair.Value as JsonObject))),
					spec);
				if (again is not null)
				{
					return Result.Fail<Done>(again);
				}

				document.Instances[spec.Id.Value] = paths.RegistryEntry(spec);
				document.Default ??= spec.Id;
				return Done.Ok;
			}, ct).ConfigureAwait(false);
			if (!written.IsSuccess)
			{
				return Abandon(paths, written.Failure!);
			}

			return new InstanceCreatedPayload
			{
				Id = spec.Id.Value,
				Root = paths.Root,
				Unit = paths.Unit,
				Port = spec.Port,
				TelemetryMod = telemetry,
			};
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException
											  or JsonException or InvalidOperationException)
		{
			// Дерево создано этим вызовом, удалять его безопасно.
			return created ? Abandon(paths, Failure.Unavailable(exception.Message)) : Failure.Unavailable(exception.Message);
		}
		catch
		{
			if (created)
			{
				TryDelete(paths.Root);
			}

			throw;
		}
	}

	/// <summary>
	/// Юнит из шаблона, <c>daemon-reload</c>, <c>enable</c>. Только для сервера, пути которого
	/// вывел демон.
	/// </summary>
	/// <remarks>
	/// Кроме имени юнита и транспорта сверяются каталоги программы, данных и FIFO. Шаблон
	/// заполняется выведенными значениями, и при расхождении с реестром юнит получился бы для
	/// другого сервера.
	/// </remarks>
	public async Task<Result<ServiceInstalledPayload>> InstallServiceAsync(InstanceId instance, CancellationToken ct)
	{
		var configuration = await ConfigurationAsync(ct).ConfigureAwait(false);
		if (!configuration.IsSuccess)
		{
			return configuration.Failure!;
		}

		var snapshot = await registry.LoadAsync(ct).ConfigureAwait(false);
		if ((snapshot as RegistrySnapshot.Available)?.Find(instance) is not { } server)
		{
			return Failure.NotFound($"Unknown instance {instance.Value}");
		}

		var layout = CreatedLayout(configuration.Value, server);
		if (!layout.IsSuccess)
		{
			return layout.Failure!;
		}

		var paths = layout.Value;
		if (server.Unit != paths.Unit
			|| server.ProgramDirectory != paths.ProgramDirectory
			|| server.DataDirectory != paths.DataDirectory
			|| server.ConsoleFifo != paths.ConsoleFifo)
		{
			return Failure.Conflict("This instance was not created by the panel and its unit is managed elsewhere");
		}

		if (server.CommandTransport != paths.CommandTransport)
		{
			return Failure.Conflict(paths.Layout == PlatformLayout.Windows
				? "Only the supervised transport is available on Windows"
				: "Only the fifo transport has a generated unit");
		}

		return await platform.InstallServiceAsync(paths, server, ct).ConfigureAwait(false);
	}

	/// <summary>
	/// Остановить и снять юнит, стереть дерево, убрать сервер из реестра. Только для сервера, пути
	/// которого вывел демон.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Сервер, заведённый вручную до панели, указывает на каталоги, которыми панель не владеет;
	/// такие серверы не удаляются.
	/// </para>
	/// <para>
	/// Если дерево стёрто не полностью, отказ перечисляет остаток, а запись реестра остаётся,
	/// чтобы оператор мог повторить удаление.
	/// </para>
	/// <para>
	/// Реестр только для чтения (версия 1) проверяется до остановки юнита, а не после удаления
	/// файлов.
	/// </para>
	/// </remarks>
	public async Task<Result<InstanceDeletedPayload>> DeleteInstanceAsync(InstanceId instance, CancellationToken ct)
	{
		var configuration = await ConfigurationAsync(ct).ConfigureAwait(false);
		if (!configuration.IsSuccess)
		{
			return configuration.Failure!;
		}

		var snapshot = await registry.LoadAsync(ct).ConfigureAwait(false);
		if (snapshot is not RegistrySnapshot.Available registered)
		{
			return Failure.Unavailable(((RegistrySnapshot.Unavailable) snapshot).Detail);
		}

		if (registered.Find(instance) is not { } server)
		{
			return Failure.NotFound($"Unknown instance {instance.Value}");
		}

		if (registered.Servers.Count == 1)
		{
			return Failure.Conflict("Cannot delete the only instance; create another server first");
		}

		if (configuration.Value.IsReadOnlyRegistry)
		{
			return Failure.Conflict(
				"The registry is read-only while /etc/vs-panel.json is version 1; run scripts/install.sh to migrate");
		}

		var layout = CreatedLayout(configuration.Value, server);
		if (!layout.IsSuccess)
		{
			return layout.Failure!;
		}

		var paths = layout.Value;
		if (server.Unit != paths.Unit
			|| server.DataDirectory != paths.DataDirectory
			|| server.ProgramDirectory != paths.ProgramDirectory)
		{
			return Failure.Conflict("This instance was not created by the panel; its files are managed elsewhere");
		}

		var removed = await platform.RemoveServiceAsync(paths, server, ct).ConfigureAwait(false);
		if (!removed.IsSuccess)
		{
			return removed.Failure!;
		}

		try
		{
			Directory.Delete(paths.Root, recursive: true);
		}
		catch (DirectoryNotFoundException)
		{
			// Дерева уже нет.
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Service removed but {paths.Root} could not be erased: {exception.Message}");
		}

		var written = await editor.EditAsync(document =>
		{
			var remaining = document.Registered;
			if (!remaining.Contains(instance))
			{
				// Запись уже удалил кто-то другой.
				return Result.Ok(document.Default ?? instance);
			}

			var next = RegistryRules.DefaultAfterRemoving(remaining, document.Default, instance);
			document.Instances.Remove(instance.Value);
			document.Default = next;
			return Result.Ok(next);
		}, ct).ConfigureAwait(false);
		if (!written.IsSuccess)
		{
			return written.Failure!;
		}

		return new InstanceDeletedPayload
		{
			Id = instance.Value,
			Root = paths.Root,
			Unit = server.Unit,
			Default = written.Value.Value,
		};
	}

	/// <summary>Журнал службы панели, общий для всех серверов.</summary>
	public Task<Result<PanelLogsPayload>> PanelLogsAsync(string lines, CancellationToken ct)
	{
		if (!int.TryParse(lines.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var requested))
		{
			return Task.FromResult<Result<PanelLogsPayload>>(Failure.Invalid("Invalid line count"));
		}

		return platform.PanelLogsAsync(Math.Clamp(requested, 20, 2000), ct);
	}

	/// <summary>Одна строка из разрешённых шести; файл root:root 0640.</summary>
	public async Task<Result<PanelEnvironmentSetPayload>> SetPanelEnvironmentAsync(PanelSetting setting, CancellationToken ct)
	{
		var configuration = await ConfigurationAsync(ct).ConfigureAwait(false);
		if (!configuration.IsSuccess)
		{
			return configuration.Failure!;
		}

		var path = configuration.Value.PanelEnvFile;
		string[] lines;
		try
		{
			var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
			lines = text.Split('\n');
			if (text.EndsWith('\n'))
			{
				lines = lines[..^1];
			}

			// Как splitlines(): \r тоже срезается, CRLF в файле окружения считается поломкой.
			lines = [.. lines.Select(line => line.TrimEnd('\r'))];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Cannot read panel environment: {exception.Message}");
		}

		var written = await WriteAtomicallyAsync(path, setting.Rewrite(lines), mode640, ct).ConfigureAwait(false);
		return written.IsSuccess
			? new PanelEnvironmentSetPayload { Key = setting.Key }
			: written.Failure!;
	}

	/// <summary>
	/// Каталог новых серверов (новое в 2.0): проверка пути, предков и самого каталога, затем
	/// запись <c>instances_root</c> в <c>vs-panel.json</c>.
	/// </summary>
	/// <remarks>
	/// Созданные серверы не переезжают: их пути записаны в реестре. Прежний каталог попадает в
	/// <c>former_instances_roots</c>, его серверы по-прежнему считаются созданными панелью, их
	/// можно удалить и переустановить (<see cref="CreatedLayout"/>).
	/// </remarks>
	public async Task<Result<InstancesRootSetPayload>> SetInstancesRootAsync(string raw, CancellationToken ct)
	{
		var configuration = await ConfigurationAsync(ct).ConfigureAwait(false);
		if (!configuration.IsSuccess)
		{
			return configuration.Failure!;
		}

		var current = configuration.Value;
		if (current.IsReadOnlyRegistry)
		{
			return Failure.Conflict("Каталог серверов меняется только в настройках панели версии 2: переустановите панель");
		}

		if (current.Layout != platform.Layout)
		{
			return Failure.Unavailable("Раскладка в настройках панели не совпадает с платформой демона");
		}

		var root = Choose(current, raw);
		if (!root.IsSuccess)
		{
			return root.Failure!;
		}

		var chosen = root.Value;
		var owned = Same(chosen, current.InstancesRoot) || current.FormerInstancesRoots.Any(path => Same(chosen, path));
		var secured = await platform.SecureServersRootAsync(chosen, owned, ct).ConfigureAwait(false);
		if (!secured.IsSuccess)
		{
			return secured.Failure!;
		}

		if (!Same(chosen, current.InstancesRoot))
		{
			string text;
			try
			{
				text = await File.ReadAllTextAsync(panelConfigPath, ct).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				return Failure.Unavailable($"Не читается {panelConfigPath}: {exception.Message}");
			}

			var rewritten = RewriteInstancesRoot(text, chosen, current.InstancesRoot);
			if (!rewritten.IsSuccess)
			{
				return rewritten.Failure!;
			}

			var written = await WriteAtomicallyAsync(panelConfigPath, rewritten.Value, mode644, ct).ConfigureAwait(false);
			if (!written.IsSuccess)
			{
				return written.Failure!;
			}
		}

		return new InstancesRootSetPayload { InstancesRoot = chosen.Path, Previous = current.InstancesRoot };
	}

	/// <summary>
	/// Текст <c>vs-panel.json</c> с новым <c>instances_root</c>. Остальные ключи и их порядок
	/// сохраняются; прежний каталог добавляется в <c>former_instances_roots</c>, новый оттуда
	/// убирается.
	/// </summary>
	public static Result<string> RewriteInstancesRoot(string text, ServersRoot chosen, string previous)
	{
		ArgumentNullException.ThrowIfNull(chosen);
		ArgumentNullException.ThrowIfNull(previous);
		JsonObject document;
		try
		{
			if (JsonNode.Parse(text) is not JsonObject parsed)
			{
				return Failure.Invalid("Настройки панели не являются объектом JSON");
			}

			document = parsed;
		}
		catch (JsonException exception)
		{
			return Failure.Invalid($"Не удалось разобрать настройки панели: {exception.Message}");
		}

		var comparer = chosen.Layout == PlatformLayout.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		var former = (document["former_instances_roots"] as JsonArray ?? [])
			.Select(item => item is JsonValue value && value.TryGetValue<string>(out var path) ? path : null)
			.OfType<string>()
			.Where(path => path.Length > 0 && !Same(chosen, path))
			.ToList();
		if (previous.Length > 0 && !Same(chosen, previous) && !former.Contains(previous, comparer))
		{
			former.Add(previous);
		}

		document["instances_root"] = chosen.Path;
		document["former_instances_roots"] = new JsonArray([.. former.Select(path => (JsonNode?) path)]);
		return document.ToJsonString(configJson) + "\n";
	}

	private static bool Same(ServersRoot chosen, string path) =>
		chosen.Is(ServersRoot.FromConfiguration(path, chosen.Layout));

	private static readonly JsonSerializerOptions configJson = new()
	{
		WriteIndented = true,
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	/// <summary>Выбор каталога серверов по правилам <see cref="ServersRoot"/> и каталогам этой панели.</summary>
	private Result<ServersRoot> Choose(PanelConfiguration configuration, string raw) =>
		ServersRoot.Create(
			raw,
			configuration.Layout,
			exclusive: [configuration.RunDirectory, configuration.StagingDirectory, configuration.TelemetryDirectory],
			containers: [Parent(configuration.InstancesFile), Parent(configuration.PanelEnvFile), Parent(panelConfigPath)]);

	private static string Parent(string path) =>
		path.Length == 0 ? string.Empty : Path.GetDirectoryName(path) ?? string.Empty;

	/// <summary>
	/// Раскладка сервера под каталогом серверов, в котором он создан: текущим или прежним.
	/// Если такого нет, берётся текущий, и сверка путей откажет, как серверу не от панели.
	/// </summary>
	private Result<InstanceLayout> CreatedLayout(PanelConfiguration configuration, RegisteredServer server)
	{
		foreach (var former in configuration.FormerInstancesRoots)
		{
			var layout = Layout(configuration with { InstancesRoot = former }, server.Id);
			if (layout.IsSuccess && layout.Value.ProgramDirectory == server.ProgramDirectory)
			{
				return layout;
			}
		}

		return Layout(configuration, server.Id);
	}

	public Task<Result<PanelRestartPayload>> RestartPanelAsync(CancellationToken ct) => platform.RestartPanelAsync(ct);

	/// <summary>Распаковать пакет рядом и переместить каталог программы на место.</summary>
	private static async Task<Result<Done>> ExtractProgramAsync(
		string package,
		PackageLayout details,
		InstanceLayout paths,
		CancellationToken ct)
	{
		var extractRoot = Path.Combine(
			paths.Root,
			".vs-instance-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant());
		try
		{
			Directory.CreateDirectory(extractRoot);
			GameFiles.SetMode(extractRoot, mode700);
			var extracted = await PackageArchive.ExtractAsync(package, extractRoot, ct).ConfigureAwait(false);
			if (!extracted.IsSuccess)
			{
				return extracted.Failure!;
			}

			var candidate = details.Root.Length > 0 ? Path.Combine(extractRoot, details.Root) : extractRoot;
			if (!File.Exists(Path.Combine(candidate, ServerPackage.ServerDll)))
			{
				return Failure.Invalid("Extracted package has no server DLL");
			}

			Directory.Move(candidate, paths.ProgramDirectory);
			return Done.Ok;
		}
		finally
		{
			TryDelete(extractRoot);
		}
	}

	/// <summary>
	/// Игра пишет полную конфигурацию сама, панель записывает поверх свои значения.
	/// <c>--genconfig</c> работает только на пустом dataPath, а здесь он пустой.
	/// </summary>
	private static async Task<Result<Done>> GenerateConfigAsync(
		IProvisioningPlatform platform,
		string dotnet,
		ServerSpec spec,
		InstanceLayout paths,
		CancellationToken ct)
	{
		var program = Path.Combine(paths.ProgramDirectory, ServerPackage.ServerDll);
		if (!File.Exists(program))
		{
			return Failure.Unavailable("Server program directory has no VintagestoryServer.dll");
		}

		HostCommandResult result;
		try
		{
			result = await platform.GenerateConfigAsync(dotnet, paths, genconfigTimeout, ct).ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
			return Failure.Unavailable("The game did not finish --genconfig within 300 seconds");
		}

		if (!File.Exists(paths.ConfigFile))
		{
			return Failure.Unavailable(result.Tail("The game did not create serverconfig.json"));
		}

		JsonObject generated;
		try
		{
			// ReadAllText сам снимает BOM.
			if (JsonNode.Parse(await File.ReadAllTextAsync(paths.ConfigFile, ct).ConfigureAwait(false)) is not JsonObject value)
			{
				return Failure.Unavailable("The generated serverconfig.json is not a JSON object");
			}

			generated = value;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return Failure.Unavailable($"Cannot read the generated serverconfig.json: {exception.Message}");
		}

		var applied = InitialConfig.Apply(generated, spec, paths);
		var options = new JsonSerializerOptions
		{
			WriteIndented = true,
			Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		};
		await File.WriteAllTextAsync(paths.ConfigFile, applied.ToJsonString(options) + "\n", new UTF8Encoding(false), ct)
			.ConfigureAwait(false);
		GameFiles.SetMode(paths.ConfigFile, mode640);
		return Done.Ok;
	}

	/// <summary>
	/// Копирует самый новый архив мода телеметрии, оставленный установщиком, чтобы можно было
	/// проверить готовность нового сервера. Если архивов нет, возвращает пустую строку.
	/// </summary>
	private static string SeedTelemetryMod(string telemetryDirectory, string modsDirectory)
	{
		if (telemetryDirectory.Length == 0 || !Directory.Exists(telemetryDirectory))
		{
			return string.Empty;
		}

		var newest = Directory.EnumerateFiles(telemetryDirectory, "vspaneltelemetry_*.zip")
			.Select(path => new FileInfo(path))
			.Where(info => info is { Exists: true, LinkTarget: null })
			.OrderBy(info => info.Name, StringComparer.Ordinal)
			.LastOrDefault();
		if (newest is null)
		{
			return string.Empty;
		}

		var target = Path.Combine(modsDirectory, newest.Name);
		File.Copy(newest.FullName, target, overwrite: true);
		GameFiles.SetMode(target, mode644);
		return newest.Name;
	}

	/// <summary>Идентификатор или порт уже заняты.</summary>
	private static Failure? Clash(IEnumerable<(string Id, int Port)> registered, ServerSpec spec)
	{
		foreach (var (id, port) in registered)
		{
			if (id == spec.Id.Value)
			{
				return Failure.Conflict($"Instance {spec.Id.Value} already exists");
			}

			if (port == spec.Port)
			{
				return Failure.Conflict($"Port {spec.Port.ToString(CultureInfo.InvariantCulture)} is already used by instance {id}");
			}
		}

		return null;
	}

	private static int PortOf(JsonObject? entry) =>
		entry?["port"] is JsonValue value && value.TryGetValue<int>(out var port) ? port : 0;

	private static Failure Abandon(InstanceLayout paths, Failure reason)
	{
		TryDelete(paths.Root);
		return reason;
	}

	/// <summary>Атомарная запись текста рядом с целью; права Unix ставятся там, где они есть.</summary>
	internal static async Task<Result<Done>> WriteAtomicallyAsync(string path, string content, UnixFileMode mode, CancellationToken ct)
	{
		var temporary = Path.Combine(
			Path.GetDirectoryName(path)!,
			$".{Path.GetFileName(path)}.{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.tmp");
		try
		{
			await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
			GameFiles.SetMode(temporary, mode);
			File.Move(temporary, path, overwrite: true);
			return Done.Ok;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Cannot write {path}: {exception.Message}");
		}
		finally
		{
			if (File.Exists(temporary))
			{
				File.Delete(temporary);
			}
		}
	}

	/// <summary>
	/// Раскладка сервера по настройкам панели. Раскладка из настроек должна совпадать с
	/// платформой демона: сервер с путями Windows, созданный на Linux, не запустится.
	/// </summary>
	private Result<InstanceLayout> Layout(PanelConfiguration configuration, InstanceId id) =>
		configuration.Layout == platform.Layout
			? InstanceLayout.Create(configuration.InstancesRoot, configuration.RunDirectory, id, configuration.Layout)
			: Failure.Unavailable("Раскладка в настройках панели не совпадает с платформой демона");

	private async Task<Result<PanelConfiguration>> ConfigurationAsync(CancellationToken ct)
	{
		try
		{
			return PanelConfiguration.Parse(await File.ReadAllTextAsync(panelConfigPath, ct).ConfigureAwait(false));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Не читается {panelConfigPath}: {exception.Message}");
		}
	}

	internal static void TryDelete(string directory)
	{
		try
		{
			if (Directory.Exists(directory))
			{
				Directory.Delete(directory, recursive: true);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Как ignore_errors=True: остаток виден оператору по пути в тексте отказа.
		}
	}
}
