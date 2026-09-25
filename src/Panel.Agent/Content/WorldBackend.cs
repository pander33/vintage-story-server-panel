using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Panel.Content.Domain;
using Panel.Content.Ports;
using Panel.Contracts;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Content;

/// <summary>
/// Миры одного сервера на диске.
/// </summary>
/// <remarks>
/// Активный мир задаёт <c>WorldConfig.SaveFileLocation</c> в <c>serverconfig.json</c>.
/// Поэтому смена активного мира — правка настроек, и она требует перезапуска.
/// </remarks>
public sealed class WorldBackend
{
	private readonly TimeProvider clock;
	private readonly StagingArea staging;

	public WorldBackend(StagingArea staging, TimeProvider clock)
	{
		ArgumentNullException.ThrowIfNull(staging);
		ArgumentNullException.ThrowIfNull(clock);

		this.staging = staging;
		this.clock = clock;
	}

	public Result<WorldCollection> List(RegisteredServer server, bool serverRunning)
	{
		if (server.WorldDirectory.Length == 0)
		{
			return Failure.Unavailable("Каталог миров не задан в реестре");
		}

		Directory.CreateDirectory(server.WorldDirectory);
		var active = ActiveWorldName(server);

		var worlds = new List<WorldFile>();
		foreach (var path in Directory.EnumerateFiles(server.WorldDirectory, "*" + WorldFileName.Extension))
		{
			var info = new FileInfo(path);
			if (info is not { Exists: true, LinkTarget: null })
			{
				continue;
			}

			var name = WorldFileName.Create(info.Name);
			if (name.IsSuccess)
			{
				worlds.Add(new WorldFile(
					name.Value,
					info.Length,
					info.LastWriteTimeUtc,
					string.Equals(info.Name, active, StringComparison.Ordinal)));
			}
		}

		return WorldCollection.Of(worlds, serverRunning);
	}

	public async Task<Result<WorldFile>> ImportAsync(
		RegisteredServer server,
		StagedFile staged,
		WorldFileName name,
		bool serverRunning,
		CancellationToken ct)
	{
		var collection = List(server, serverRunning);
		if (!collection.IsSuccess)
		{
			return Result.Fail<WorldFile>(collection.Failure!);
		}

		var allowed = collection.Value.CanAdd(name);
		if (!allowed.IsSuccess)
		{
			return Result.Fail<WorldFile>(allowed.Failure!);
		}

		var source = await staging.ResolveAsync(staged, WorldFileName.Extension, ct).ConfigureAwait(false);
		if (!source.IsSuccess)
		{
			return Result.Fail<WorldFile>(source.Failure!);
		}

		var signature = await GameFiles.CheckWorldSignatureAsync(source.Value, ct).ConfigureAwait(false);
		if (!signature.IsSuccess)
		{
			return Result.Fail<WorldFile>(signature.Failure!);
		}

		var target = Path.Combine(server.WorldDirectory, name.Value);
		var written = await GameFiles.ReplaceAsync(
			source.Value,
			target,
			UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
			ownedByGame: true,
			ct).ConfigureAwait(false);

		return written.IsSuccess
			? new WorldFile(name, new FileInfo(target).Length, clock.GetUtcNow(), Active: false)
			: Result.Fail<WorldFile>(written.Failure!);
	}

	/// <summary>
	/// Выгрузить мир в промежуточное хранение.
	/// </summary>
	/// <remarks>
	/// Через <c>sqlite3_backup</c>, а не копированием файла: работающий сервер постоянно пишет
	/// в базу, и побайтовая копия на ходу окажется повреждённой.
	/// </remarks>
	public async Task<Result<WorldExport>> ExportAsync(
		RegisteredServer server,
		WorldFileName name,
		StagedFile staged,
		CancellationToken ct)
	{
		var source = Path.Combine(server.WorldDirectory, name.Value);
		if (!GameFiles.IsPlainFile(source))
		{
			return Failure.NotFound("Мир не найден");
		}

		var target = await staging.ReserveAsync(staged, ct).ConfigureAwait(false);
		if (!target.IsSuccess)
		{
			return Result.Fail<WorldExport>(target.Failure!);
		}

		try
		{
			await using var from = new SqliteConnection(
				new SqliteConnectionStringBuilder
				{
					DataSource = source,
					Mode = SqliteOpenMode.ReadOnly,
				}.ToString());
			await using var to = new SqliteConnection(
				new SqliteConnectionStringBuilder { DataSource = target.Value }.ToString());

			await from.OpenAsync(ct).ConfigureAwait(false);
			await to.OpenAsync(ct).ConfigureAwait(false);
			from.BackupDatabase(to);
		}
		catch (SqliteException exception)
		{
			File.Delete(target.Value);
			return Failure.Unavailable($"Не удалось снять согласованный снимок мира: {exception.Message}");
		}

		GameFiles.SetMode(target.Value, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		return new WorldExport(name, staged, new FileInfo(target.Value).Length);
	}

	public Result<WorldFile> Clone(
		RegisteredServer server,
		WorldFileName source,
		WorldFileName target,
		bool serverRunning)
	{
		var collection = List(server, serverRunning);
		if (!collection.IsSuccess)
		{
			return Result.Fail<WorldFile>(collection.Failure!);
		}

		var allowed = collection.Value.CanAdd(target).Bind(_ => collection.Value.Find(source));
		if (!allowed.IsSuccess)
		{
			return allowed;
		}

		var targetPath = Path.Combine(server.WorldDirectory, target.Value);
		File.Copy(Path.Combine(server.WorldDirectory, source.Value), targetPath);

		return new WorldFile(target, new FileInfo(targetPath).Length, clock.GetUtcNow(), Active: false);
	}

	public async Task<Result<Done>> ActivateAsync(
		RegisteredServer server,
		WorldFileName name,
		bool serverRunning,
		CancellationToken ct)
	{
		var collection = List(server, serverRunning);
		if (!collection.IsSuccess)
		{
			return Result.Fail<Done>(collection.Failure!);
		}

		var found = collection.Value.CanModify().Bind(_ => collection.Value.Find(name));

		return found.IsSuccess
			? await WriteWorldConfigAsync(server, name, plan: null, ct).ConfigureAwait(false)
			: Result.Fail<Done>(found.Failure!);
	}

	/// <summary>
	/// Записать параметры нового мира. Файл базы создаёт игра при первом запуске, панель
	/// задаёт только имя и параметры.
	/// </summary>
	public async Task<Result<Done>> CreateAsync(
		RegisteredServer server,
		WorldPlan plan,
		bool serverRunning,
		CancellationToken ct)
	{
		var collection = List(server, serverRunning);
		if (!collection.IsSuccess)
		{
			return Result.Fail<Done>(collection.Failure!);
		}

		var allowed = collection.Value.CanAdd(plan.File);

		return allowed.IsSuccess
			? await WriteWorldConfigAsync(server, plan.File, plan, ct).ConfigureAwait(false)
			: allowed;
	}

	public Result<StoredWorld> PutAway(
		RegisteredServer server,
		WorldFileName name,
		WorldDisposition disposition,
		bool serverRunning)
	{
		var collection = List(server, serverRunning);
		if (!collection.IsSuccess)
		{
			return Result.Fail<StoredWorld>(collection.Failure!);
		}

		var removable = collection.Value.CanRemove(name);
		if (!removable.IsSuccess)
		{
			return Result.Fail<StoredWorld>(removable.Failure!);
		}

		var directory = StoreDirectory(server, disposition);
		Directory.CreateDirectory(directory);

		var target = GameFiles.FreeName(directory, GameFiles.Stamp(clock.GetUtcNow()), name.Value);
		File.Move(Path.Combine(server.WorldDirectory, name.Value), target);

		var info = new FileInfo(target);
		var stored = StoredWorldName.Create(info.Name);

		return stored.IsSuccess
			? new StoredWorld(stored.Value, disposition, info.Length, info.LastWriteTimeUtc)
			: Result.Fail<StoredWorld>(stored.Failure!);
	}

	public Result<IReadOnlyList<StoredWorld>> ListStored(RegisteredServer server)
	{
		var result = new List<StoredWorld>();

		foreach (var disposition in new[] { WorldDisposition.Archived, WorldDisposition.Trashed })
		{
			var directory = StoreDirectory(server, disposition);
			if (!Directory.Exists(directory))
			{
				continue;
			}

			foreach (var path in Directory.EnumerateFiles(directory, "*" + WorldFileName.Extension))
			{
				var info = new FileInfo(path);
				if (info is not { Exists: true, LinkTarget: null })
				{
					continue;
				}

				var name = StoredWorldName.Create(info.Name);
				if (name.IsSuccess)
				{
					result.Add(new StoredWorld(name.Value, disposition, info.Length, info.LastWriteTimeUtc));
				}
			}
		}

		result.Sort((left, right) => right.Modified.CompareTo(left.Modified));
		return Result.Ok<IReadOnlyList<StoredWorld>>(result);
	}

	public async Task<Result<WorldFile>> RestoreStoredAsync(
		RegisteredServer server,
		WorldDisposition disposition,
		StoredWorldName stored,
		WorldFileName target,
		bool serverRunning,
		CancellationToken ct)
	{
		var collection = List(server, serverRunning);
		if (!collection.IsSuccess)
		{
			return Result.Fail<WorldFile>(collection.Failure!);
		}

		var allowed = collection.Value.CanAdd(target);
		if (!allowed.IsSuccess)
		{
			return Result.Fail<WorldFile>(allowed.Failure!);
		}

		var source = Path.Combine(StoreDirectory(server, disposition), stored.Value);
		if (!GameFiles.IsPlainFile(source))
		{
			return Failure.NotFound("Убранный мир не найден");
		}

		var signature = await GameFiles.CheckWorldSignatureAsync(source, ct).ConfigureAwait(false);
		if (!signature.IsSuccess)
		{
			return Result.Fail<WorldFile>(signature.Failure!);
		}

		var targetPath = Path.Combine(server.WorldDirectory, target.Value);
		File.Move(source, targetPath);

		return new WorldFile(target, new FileInfo(targetPath).Length, clock.GetUtcNow(), Active: false);
	}

	/// <summary>
	/// Окончательно стереть убранный мир.
	/// </summary>
	/// <remarks>
	/// Единственное безвозвратное удаление здесь: архив и корзина сами являются местом для
	/// восстановления. База мира обычно самый крупный файл, и без удаления место не освободить.
	/// </remarks>
	public Result<long> DeleteStored(RegisteredServer server, WorldDisposition disposition, StoredWorldName stored)
	{
		var source = Path.Combine(StoreDirectory(server, disposition), stored.Value);
		if (!GameFiles.IsPlainFile(source))
		{
			return Failure.NotFound("Убранный мир не найден");
		}

		var freed = new FileInfo(source).Length;
		File.Delete(source);
		return freed;
	}

	private static string StoreDirectory(RegisteredServer server, WorldDisposition disposition) =>
		Path.Combine(server.WorldDirectory, "." + WorldDispositionWire.ToWire(disposition));

	/// <summary>Имя файла активного мира из <c>serverconfig.json</c>.</summary>
	private static string? ActiveWorldName(RegisteredServer server)
	{
		try
		{
			if (JsonNode.Parse(File.ReadAllText(server.ConfigFile)) is not JsonObject root
				|| root["WorldConfig"] is not JsonObject world
				|| (string?) world["SaveFileLocation"] is not { Length: > 0 } location)
			{
				return null;
			}

			return Path.GetFileName(location.Replace('\\', '/'));
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
		{
			// Нечитаемые настройки: активный мир неизвестен, но список миров всё равно показывается.
			return null;
		}
	}

	/// <summary>
	/// Правка <c>WorldConfig</c> в <c>serverconfig.json</c> с сохранением копии.
	/// <paramref name="plan"/> равен null, когда меняется только активный мир.
	/// </summary>
	private async Task<Result<Done>> WriteWorldConfigAsync(
		RegisteredServer server,
		WorldFileName file,
		WorldPlan? plan,
		CancellationToken ct)
	{
		JsonObject root;
		try
		{
			root = JsonNode.Parse(await File.ReadAllTextAsync(server.ConfigFile, ct).ConfigureAwait(false))
				as JsonObject ?? [];
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Не читается serverconfig.json: {exception.Message}");
		}

		if (root["WorldConfig"] is not JsonObject world)
		{
			world = [];
			root["WorldConfig"] = world;
		}

		world["SaveFileLocation"] = Path.Combine(server.WorldDirectory, file.Value);

		if (plan is not null)
		{
			world["WorldName"] = plan.DisplayName;
			// Пустое зерно означает, что его выберет игра: null, а не "".
			world["Seed"] = plan.Seed.Length > 0 ? plan.Seed : null;
			world["PlayStyle"] = PlayStyleWire.ToWire(plan.Style);
			world["PlayStyleLangCode"] = PlayStyleWire.LanguageCode(plan.Style);
		}

		var temporary = Path.Combine(
			Path.GetDirectoryName(server.ConfigFile)!,
			$".serverconfig.panel-{Environment.ProcessId}.json");

		try
		{
			await File.WriteAllTextAsync(temporary, root.ToJsonString(PanelJson.Indented) + "\n", ct)
				.ConfigureAwait(false);

			if (File.Exists(server.ConfigFile))
			{
				var backup = GameFiles.FreeName(
					Path.GetDirectoryName(server.ConfigFile)!,
					Path.GetFileName(server.ConfigFile) + ".panel",
					GameFiles.Stamp(clock.GetUtcNow()) + ".bak");
				File.Copy(server.ConfigFile, backup);
			}

			return await GameFiles.ReplaceAsync(
				temporary,
				server.ConfigFile,
				UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
				ownedByGame: true,
				ct).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Не удалось записать serverconfig.json: {exception.Message}");
		}
		finally
		{
			if (File.Exists(temporary))
			{
				File.Delete(temporary);
			}
		}
	}
}
