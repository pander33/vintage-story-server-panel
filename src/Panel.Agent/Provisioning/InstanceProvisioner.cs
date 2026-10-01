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

/// <summary>Создание и импорт экземпляра с общими проверками пакета и очисткой до записи в реестр.</summary>
internal sealed class InstanceProvisioner(ProvisioningConfiguration settings, IRegistryStore registry, RegistryEditor editor, StagingArea staging, IProvisioningPlatform platform)
{
	private static readonly UnixFileMode mode755 =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
		| UnixFileMode.GroupRead | UnixFileMode.GroupExecute
		| UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

	private static readonly UnixFileMode mode750 =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
		| UnixFileMode.GroupRead | UnixFileMode.GroupExecute;

	/// <summary>Создать дерево сервера и записать сервер в реестр.</summary>
	public async Task<Result<InstanceCreatedPayload>> CreateAsync(JsonNode? specification, CancellationToken ct)
	{
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
			return Failure.Conflict("Creating servers needs /etc/vs-panel.json version 2; run scripts/install.sh");
		}

		var snapshot = await registry.LoadAsync(ct).ConfigureAwait(false);
		if (snapshot is not RegistrySnapshot.Available registered)
		{
			return Failure.Unavailable(((RegistrySnapshot.Unavailable) snapshot).Detail);
		}

		var clash = InstanceProgram.Clash(registered.Servers.Select(server => (server.Id.Value, InstanceProgram.PortOf(server.Raw))), spec);
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
			var root = settings.Choose(configuration.Value, paths.InstancesRoot);
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

			var extracted = await InstanceProgram.ExtractProgramAsync(package.Value, details.Value, paths, ct).ConfigureAwait(false);
			if (!extracted.IsSuccess)
			{
				return InstanceProgram.Abandon(paths, extracted.Failure!);
			}

			// До --genconfig, потому что игра пишет конфигурацию от своего имени. Второй проход
			// подбирает то, что root добавил после.
			var owned = await platform.GiveToGameAsync(paths.Root, ct).ConfigureAwait(false);
			if (!owned.IsSuccess)
			{
				return InstanceProgram.Abandon(paths, owned.Failure!);
			}

			var configured = await InstanceProgram.GenerateConfigAsync(platform, dotnet, spec, paths, ct).ConfigureAwait(false);
			if (!configured.IsSuccess)
			{
				return InstanceProgram.Abandon(paths, configured.Failure!);
			}

			var telemetry = InstanceProgram.SeedTelemetryMod(configuration.Value.TelemetryDirectory, paths.ModsDirectory);
			owned = await platform.GiveToGameAsync(paths.Root, ct).ConfigureAwait(false);
			if (!owned.IsSuccess)
			{
				return InstanceProgram.Abandon(paths, owned.Failure!);
			}

			var written = await editor.EditAsync(document =>
			{
				// Проверка ещё раз, под блокировкой реестра: снимок выше мог устареть.
				var again = InstanceProgram.Clash(
					document.Instances.Select(pair => (pair.Key, InstanceProgram.PortOf(pair.Value as JsonObject))),
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
				return InstanceProgram.Abandon(paths, written.Failure!);
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
			return created ? InstanceProgram.Abandon(paths, Failure.Unavailable(exception.Message)) : Failure.Unavailable(exception.Message);
		}
		catch
		{
			if (created)
			{
				ProvisioningFiles.TryDelete(paths.Root);
			}

			throw;
		}
	}

}
