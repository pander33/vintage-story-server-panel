using Panel.Content.Domain;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Provisioning.Domain;

namespace Panel.Agent.Content;

/// <summary>
/// Резервные копии одного сервера.
/// </summary>
/// <remarks>
/// Копия — переносимый пакет с активным миром, настройками без секретов и модами в <c>.tar.gz</c>.
/// Снимается при остановленной службе для согласованного снимка мира.
/// </remarks>
public sealed class BackupBackend
{
	private readonly TimeProvider clock;

	public BackupBackend(TimeProvider clock)
	{
		ArgumentNullException.ThrowIfNull(clock);
		this.clock = clock;
	}

	/// <summary>Снимок для передачи на другую машину, без записи в историю копий.</summary>
	public async Task<Result<long>> ExportAsync(
		RegisteredServer server, string destination, CancellationToken ct, OperationScope? scope = null)
	{
		var success = false;
		try
		{
			var written = await ServerPackageWriter.WriteAsync(server, destination, ct, scope).ConfigureAwait(false);
			success = written.IsSuccess;
			return written.IsSuccess
				? new FileInfo(destination).Length
				: Result.Fail<long>(written.Failure!);
		}
		finally
		{
			if (!success && File.Exists(destination))
			{
				File.Delete(destination);
			}
		}
	}

	public Result<BackupSet> List(RegisteredServer server)
	{
		if (server.BackupDirectory.Length == 0)
		{
			return Failure.Unavailable("Каталог резервных копий не задан в реестре");
		}

		Directory.CreateDirectory(server.BackupDirectory);

		var backups = new List<BackupFile>();
		foreach (var path in Directory.EnumerateFiles(server.BackupDirectory, "*.tar.gz"))
		{
			var info = new FileInfo(path);
			if (info is not { Exists: true, LinkTarget: null })
			{
				continue;
			}

			var name = BackupName.Create(info.Name);
			if (name.IsSuccess)
			{
				backups.Add(new BackupFile(name.Value, info.Length, info.LastWriteTimeUtc));
			}
		}

		return BackupSet.Of(backups);
	}

	/// <summary>
	/// Снять копию. Архив пишется как <c>.partial</c> и переименовывается после полной проверки
	/// чтением, чтобы оборванная копия не попала в список.
	/// </summary>
	public async Task<Result<BackupFile>> CreateAsync(
		RegisteredServer server,
		string prefix,
		CancellationToken ct,
		OperationScope? scope = null)
	{
		Directory.CreateDirectory(server.BackupDirectory);

		var stamp = GameFiles.BackupStamp(clock.GetUtcNow());
		var candidate = $"{prefix}{stamp}.tar.gz";
		for (var suffix = 1; Path.Exists(Path.Combine(server.BackupDirectory, candidate)); suffix++)
		{
			candidate = $"{prefix}{stamp}-{suffix}.tar.gz";
		}
		var name = BackupName.Create(candidate);
		if (!name.IsSuccess)
		{
			return Result.Fail<BackupFile>(name.Failure!);
		}

		var final = Path.Combine(server.BackupDirectory, name.Value.Value);
		var partial = final + ".partial";

		try
		{
			var written = await ServerPackageWriter.WriteAsync(server, partial, ct, scope).ConfigureAwait(false);
			if (!written.IsSuccess)
			{
				return Result.Fail<BackupFile>(written.Failure!);
			}

			scope?.Enter(OperationStages.Check);
			var verified = await TreeArchive.JudgeTransferAsync(partial, ct).ConfigureAwait(false);
			if (!verified.IsSuccess)
			{
				return Result.Fail<BackupFile>(verified.Failure!);
			}

			File.Move(partial, final);
		}
		finally
		{
			if (File.Exists(partial))
			{
				File.Delete(partial);
			}
		}

		var info = new FileInfo(final);
		return new BackupFile(name.Value, info.Length, info.LastWriteTimeUtc);
	}

	public async Task<Result<BackupContents>> InspectAsync(
		RegisteredServer server,
		BackupName name,
		CancellationToken ct)
	{
		var source = await SelectAsync(server, name).ConfigureAwait(false);

		if (!source.IsSuccess)
		{
			return Result.Fail<BackupContents>(source.Failure!);
		}
		var verified = await TreeArchive.JudgeTransferAsync(source.Value, ct).ConfigureAwait(false);
		return verified.IsSuccess
			? await TreeArchive.InspectAsync(source.Value, ct).ConfigureAwait(false)
			: Result.Fail<BackupContents>(verified.Failure!);
	}

	/// <summary>
	/// Развернуть копию поверх каталога данных.
	/// </summary>
	/// <remarks>
	/// Порядок: страховочная копия текущего состояния, распаковка во временный каталог рядом,
	/// затем подмена переименованием. При распаковке прямо поверх данных сбой оставил бы смесь
	/// двух состояний.
	/// </remarks>
	public async Task<Result<BackupName>> RestoreAsync(
		RegisteredServer server,
		BackupName name,
		CancellationToken ct,
		OperationScope? scope = null)
	{
		var source = await SelectAsync(server, name).ConfigureAwait(false);
		if (!source.IsSuccess)
		{
			return Result.Fail<BackupName>(source.Failure!);
		}

		scope?.Enter(OperationStages.Verify);

		var inspected = await TreeArchive.JudgeTransferAsync(source.Value, ct).ConfigureAwait(false);
		if (!inspected.IsSuccess)
		{
			return Result.Fail<BackupName>(inspected.Failure!);
		}

		scope?.Enter(OperationStages.Safety);
		var safety = await CreateAsync(server, BackupName.SafetyPrefix, ct, scope).ConfigureAwait(false);
		if (!safety.IsSuccess)
		{
			return Result.Fail<BackupName>(safety.Failure!);
		}

		var data = new DirectoryInfo(server.DataDirectory);
		var parent = data.Parent?.FullName ?? throw new InvalidOperationException("У каталога данных нет родителя");
		var staging = Path.Combine(parent, $".vs-panel-restore-{Guid.NewGuid():N}");
		var displaced = GameFiles.FreeName(parent, $"{data.Name}.pre-restore", GameFiles.Stamp(clock.GetUtcNow()));

		try
		{
			scope?.Enter(OperationStages.Extract);
			var extracted = await TreeArchive.ExtractTransferAsync(source.Value, staging, ct).ConfigureAwait(false);
			if (!extracted.IsSuccess)
			{
				return Result.Fail<BackupName>(extracted.Failure!);
			}

			var restored = Path.Combine(staging, data.Name);
			if (!Directory.Exists(restored))
			{
				return Failure.Invalid("В резервной копии нет каталога данных");
			}

			var configPath = Path.Combine(restored, "serverconfig.json");
			if (File.Exists(configPath))
			{
				if (JsonNode.Parse(await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false)) is not JsonObject config)
				{
					return Failure.Invalid("Настройки сервера в копии не являются объектом JSON");
				}

				var rebased = ServerConfigPaths.Create(server.WorldDirectory, server.ModsDirectory).Rebase(config);
				if (!rebased.IsSuccess)
				{
					return Result.Fail<BackupName>(rebased.Failure!);
				}

				if (JsonNode.Parse(await File.ReadAllTextAsync(server.ConfigFile, ct).ConfigureAwait(false)) is not JsonObject local)
				{
					return Failure.Invalid("Локальные настройки сервера недоступны");
				}
				foreach (var (key, value) in local)
				{
					if (key is not "WorldConfig" and not "ModPaths" && !rebased.Value.ContainsKey(key))
					{
						rebased.Value[key] = value?.DeepClone();
					}
				}
				if (local["WorldConfig"] is JsonObject localWorld && rebased.Value["WorldConfig"] is JsonObject restoredWorld)
				{
					foreach (var (key, value) in localWorld)
					{
						if (!restoredWorld.ContainsKey(key))
						{
							restoredWorld[key] = value?.DeepClone();
						}
					}
				}
				if (!JsonNode.DeepEquals(config, rebased.Value))
				{
					await File.WriteAllTextAsync(configPath, rebased.Value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", ct)
						.ConfigureAwait(false);
				}
			}
			var preserved = PreserveLocalFiles(server.DataDirectory, restored);
			if (!preserved.IsSuccess)
			{
				return Result.Fail<BackupName>(preserved.Failure!);
			}
			var owned = await OwnRestoredTreeAsync(restored, ct).ConfigureAwait(false);
			if (!owned.IsSuccess)
			{
				return Result.Fail<BackupName>(owned.Failure!);
			}

			scope?.Enter(OperationStages.Swap);
			Directory.Move(server.DataDirectory, displaced);
			try
			{
				Directory.Move(restored, server.DataDirectory);
			}
			catch
			{
				// Вернуть прежние данные, иначе сервер останется без каталога данных.
				Directory.Move(displaced, server.DataDirectory);
				throw;
			}

			// Прежнее дерево остаётся рядом как страховка; локальные файлы также скопированы выше.
			// Страховки прошлых восстановлений убираются: иначе они копились бы без предела.
			PruneDisplaced(parent, $"{data.Name}.pre-restore", displaced);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return Failure.Unavailable($"Восстановление не завершилось: {exception.Message}");
		}
		finally
		{
			if (Directory.Exists(staging))
			{
				Directory.Delete(staging, recursive: true);
			}
		}

		return safety.Value.Name;
	}

	public async Task<Result<long>> DeleteAsync(RegisteredServer server, BackupName name, CancellationToken ct)
	{
		var set = List(server);
		if (!set.IsSuccess)
		{
			return Result.Fail<long>(set.Failure!);
		}

		var deletable = set.Value.CanDelete(name);
		if (!deletable.IsSuccess)
		{
			return Result.Fail<long>(deletable.Failure!);
		}

		var path = Path.Combine(server.BackupDirectory, name.Value);
		var freed = new FileInfo(path).Length;
		File.Delete(path);

		await Task.CompletedTask.ConfigureAwait(false);
		return freed;
	}

	/// <summary>
	/// Достать из копии один мир под новым именем, не разворачивая копию целиком: остальные
	/// данные при этом были бы перезаписаны.
	/// </summary>
	public async Task<Result<WorldFile>> RestoreWorldAsync(
		RegisteredServer server,
		BackupName backup,
		string world,
		WorldFileName target,
		CancellationToken ct,
		OperationScope? scope = null)
	{
		scope?.Enter(OperationStages.Verify);
		var source = await SelectAsync(server, backup).ConfigureAwait(false);
		if (!source.IsSuccess)
		{
			return Result.Fail<WorldFile>(source.Failure!);
		}

		var verified = await TreeArchive.JudgeTransferAsync(source.Value, ct).ConfigureAwait(false);
		if (!verified.IsSuccess)
		{
			return Result.Fail<WorldFile>(verified.Failure!);
		}
		var contents = await TreeArchive.InspectAsync(source.Value, ct).ConfigureAwait(false);
		if (!contents.IsSuccess)
		{
			return Result.Fail<WorldFile>(contents.Failure!);
		}

		scope?.Enter(OperationStages.Extracting);

		var matches = contents.Value.Worlds
			.Where(item => string.Equals(item.Name, world, StringComparison.Ordinal))
			.ToArray();

		if (matches.Length != 1)
		{
			// Мира в копии нет или в разных каталогах копии лежат одноимённые миры:
			// какой из них нужен, не угадать.
			return Failure.NotFound("В резервной копии нет ровно одного такого мира");
		}

		var targetPath = Path.Combine(server.WorldDirectory, target.Value);
		if (Path.Exists(targetPath))
		{
			return Failure.Conflict("Мир с таким именем файла уже существует");
		}

		Directory.CreateDirectory(server.WorldDirectory);
		var temporary = targetPath + $".restore-{Environment.ProcessId}";

		try
		{
			var extracted = await TreeArchive
				.ExtractOneAsync(source.Value, matches[0].Member, temporary, ct)
				.ConfigureAwait(false);

			if (!extracted.IsSuccess)
			{
				return Result.Fail<WorldFile>(extracted.Failure!);
			}

			var signature = await GameFiles.CheckWorldSignatureAsync(temporary, ct).ConfigureAwait(false);
			if (!signature.IsSuccess)
			{
				return Result.Fail<WorldFile>(signature.Failure!);
			}

			var placed = await GameFiles.ReplaceAsync(
				temporary,
				targetPath,
				UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
				ownedByGame: true,
				ct).ConfigureAwait(false);

			return placed.IsSuccess
				? new WorldFile(target, extracted.Value, clock.GetUtcNow(), Active: false)
				: Result.Fail<WorldFile>(placed.Failure!);
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
	/// Удалить перечисленные копии. Какие именно, решил домен; здесь проверяется только,
	/// что в перечне нет чужих файлов.
	/// </summary>
	public Result<IReadOnlyList<BackupName>> Remove(RegisteredServer server, IReadOnlyList<BackupName> names)
	{
		var set = List(server);
		if (!set.IsSuccess)
		{
			return Result.Fail<IReadOnlyList<BackupName>>(set.Failure!);
		}

		foreach (var name in names)
		{
			var found = set.Value.Find(name);
			if (!found.IsSuccess)
			{
				return Result.Fail<IReadOnlyList<BackupName>>(found.Failure!);
			}
		}

		var removed = new List<BackupName>();
		foreach (var name in names)
		{
			File.Delete(Path.Combine(server.BackupDirectory, name.Value));
			removed.Add(name);
		}

		return Result.Ok<IReadOnlyList<BackupName>>(removed);
	}

	public long FreeSpace(RegisteredServer server) => GameFiles.FreeSpace(server.BackupDirectory);

	private static void PruneDisplaced(string parent, string prefix, string keep)
	{
		foreach (var old in Directory.EnumerateDirectories(parent, prefix + "*"))
		{
			if (string.Equals(old, keep, StringComparison.Ordinal))
			{
				continue;
			}

			try
			{
				Directory.Delete(old, recursive: true);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// Лишняя страховка не повод считать восстановление проваленным.
			}
		}
	}

	private static async Task<Result<Done>> OwnRestoredTreeAsync(string root, CancellationToken ct)
	{
		var pending = new Stack<string>();
		pending.Push(root);
		while (pending.Count > 0)
		{
			ct.ThrowIfCancellationRequested();
			var path = pending.Pop();
			var owned = await GameFiles.ChangeOwnerAsync(path, GameFiles.GameUser, ct).ConfigureAwait(false);
			if (!owned.IsSuccess)
			{
				return owned;
			}
			if (Directory.Exists(path))
			{
				foreach (var entry in Directory.EnumerateFileSystemEntries(path))
				{
					pending.Push(entry);
				}
			}
		}
		return Done.Ok;
	}

	private static Result<Done> PreserveLocalFiles(string source, string destination)
	{
		var pending = new Stack<(string Source, string Destination)>();
		pending.Push((source, destination));
		while (pending.Count > 0)
		{
			var (directory, targetDirectory) = pending.Pop();
			foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
			{
				if (entry.LinkTarget is not null || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
				{
					return Failure.Invalid("Локальные данные содержат ссылку; восстановление остановлено");
				}
				var target = Path.Combine(targetDirectory, entry.Name);
				if (entry is DirectoryInfo child)
				{
					if (File.Exists(target))
					{
						return Failure.Invalid("Файл пакета перекрывает локальный каталог");
					}
					Directory.CreateDirectory(target);
					pending.Push((child.FullName, target));
				}
				else if (entry is FileInfo)
				{
					if (!Path.Exists(target))
					{
						File.Copy(entry.FullName, target);
					}
					else if (!File.Exists(target))
					{
						return Failure.Invalid("Каталог пакета перекрывает локальный файл");
					}
					else if (entry.Name != "serverconfig.json"
						&& !(directory.EndsWith(Path.DirectorySeparatorChar + "Saves", StringComparison.OrdinalIgnoreCase)
							&& entry.Name.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase))
						&& !(directory.EndsWith(Path.DirectorySeparatorChar + "Mods", StringComparison.OrdinalIgnoreCase)
							&& (entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
								|| entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))))
					{
						return Failure.Invalid("Файл пакета перекрывает локальный файл данных");
					}
				}
				else
				{
					return Failure.Invalid("Неизвестный тип локального файла; восстановление остановлено");
				}
			}
		}
		return Done.Ok;
	}

	private async Task<Result<string>> SelectAsync(RegisteredServer server, BackupName name)
	{
		var set = List(server);
		if (!set.IsSuccess)
		{
			return Result.Fail<string>(set.Failure!);
		}

		var found = set.Value.Find(name);
		if (!found.IsSuccess)
		{
			return Result.Fail<string>(found.Failure!);
		}

		var path = Path.Combine(server.BackupDirectory, name.Value);

		await Task.CompletedTask.ConfigureAwait(false);
		return GameFiles.IsPlainFile(path) ? path : Failure.NotFound("Резервная копия не найдена");
	}
}
