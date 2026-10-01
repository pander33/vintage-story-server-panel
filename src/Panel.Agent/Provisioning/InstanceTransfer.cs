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

/// <summary>Проверка и импорт переносимого пакета с проверенной длиной и SHA256.</summary>
internal sealed class InstanceTransfer(ProvisioningConfiguration settings, IRegistryStore registry, RegistryEditor editor, StagingArea staging, IProvisioningPlatform platform)
{
	private static readonly JsonSerializerOptions configJson = new()
	{
		WriteIndented = true,
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	private static readonly UnixFileMode mode755 =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
		| UnixFileMode.GroupRead | UnixFileMode.GroupExecute
		| UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

	private static readonly UnixFileMode mode750 =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
		| UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

	private static readonly UnixFileMode mode600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

	private static readonly UnixFileMode mode640 =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

	/// <summary>Проверить привязанный к длине и SHA256 переносимый пакет без записи его содержимого.</summary>
	public async Task<Result<TransferInspectionPayload>> InspectTransferAsync(string name, string sha256, string length, CancellationToken ct)
	{
		var source = await TransferSourceAsync(name, sha256, length, ct).ConfigureAwait(false);
		if (!source.IsSuccess)
		{
			return source.Failure!;
		}

		var configuration = await settings.ReadAsync(ct).ConfigureAwait(false);
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
		var configuration = await settings.ReadAsync(ct).ConfigureAwait(false);
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

		var clash = InstanceProgram.Clash(snapshot.Servers.Select(server => (server.Id.Value, InstanceProgram.PortOf(server.Raw))), spec);
		if (clash is not null)
		{
			return clash;
		}

		var layout = settings.Layout(configuration.Value, spec.Id);
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

			var extracted = await InstanceProgram.ExtractProgramAsync(package.Value, details.Value, paths, ct).ConfigureAwait(false);
			if (!extracted.IsSuccess)
			{
				return extracted.Failure!;
			}

			var owned = await platform.GiveToGameAsync(paths.Root, ct).ConfigureAwait(false);
			if (!owned.IsSuccess)
			{
				return owned.Failure!;
			}

			var configured = await InstanceProgram.GenerateConfigAsync(platform, prepared.Value, spec, paths, ct).ConfigureAwait(false);
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

			ProvisioningFiles.TryDelete(imported);
			var telemetry = InstanceProgram.SeedTelemetryMod(configuration.Value.TelemetryDirectory, paths.ModsDirectory);
			owned = await platform.GiveToGameAsync(paths.Root, ct).ConfigureAwait(false);
			if (!owned.IsSuccess)
			{
				return owned.Failure!;
			}

			var written = await editor.EditAsync(document =>
			{
				var again = InstanceProgram.Clash(document.Instances.Select(pair => (pair.Key, InstanceProgram.PortOf(pair.Value as JsonObject))), spec);
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
				ProvisioningFiles.TryDelete(paths.Root);
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
			var root = settings.Choose(configuration, configuration.InstancesRoot);
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
}
