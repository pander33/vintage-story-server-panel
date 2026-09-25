using Panel.Content.Domain;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Content;

/// <summary>
/// Резервные копии одного сервера.
/// </summary>
/// <remarks>
/// Копия — весь каталог данных в <c>.tar.gz</c>. Снимается при остановленной службе: файлы,
/// открытые работающим сервером, в согласованную копию не попадут.
/// </remarks>
public sealed class BackupBackend
{
	private readonly TimeProvider clock;

	public BackupBackend(TimeProvider clock)
	{
		ArgumentNullException.ThrowIfNull(clock);
		this.clock = clock;
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
		CancellationToken ct)
	{
		Directory.CreateDirectory(server.BackupDirectory);

		var name = BackupName.Create($"{prefix}{GameFiles.BackupStamp(clock.GetUtcNow())}.tar.gz");
		if (!name.IsSuccess)
		{
			return Result.Fail<BackupFile>(name.Failure!);
		}

		var final = Path.Combine(server.BackupDirectory, name.Value.Value);
		var partial = final + ".partial";

		try
		{
			var written = await TreeArchive.WriteAsync(server.DataDirectory, partial, ct).ConfigureAwait(false);
			if (!written.IsSuccess)
			{
				return Result.Fail<BackupFile>(written.Failure!);
			}

			var verified = await TreeArchive.InspectAsync(partial, ct).ConfigureAwait(false);
			if (!verified.IsSuccess)
			{
				return Result.Fail<BackupFile>(verified.Failure!);
			}

			File.Move(partial, final, overwrite: true);
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

		return source.IsSuccess
			? await TreeArchive.InspectAsync(source.Value, ct).ConfigureAwait(false)
			: Result.Fail<BackupContents>(source.Failure!);
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
		CancellationToken ct)
	{
		var source = await SelectAsync(server, name).ConfigureAwait(false);
		if (!source.IsSuccess)
		{
			return Result.Fail<BackupName>(source.Failure!);
		}

		var inspected = await TreeArchive.InspectAsync(source.Value, ct).ConfigureAwait(false);
		if (!inspected.IsSuccess)
		{
			return Result.Fail<BackupName>(inspected.Failure!);
		}

		var safety = await CreateAsync(server, BackupName.SafetyPrefix, ct).ConfigureAwait(false);
		if (!safety.IsSuccess)
		{
			return Result.Fail<BackupName>(safety.Failure!);
		}

		var data = new DirectoryInfo(server.DataDirectory);
		var parent = data.Parent?.FullName ?? throw new InvalidOperationException("У каталога данных нет родителя");
		var staging = Path.Combine(parent, $".vs-panel-restore-{Environment.ProcessId}");
		var displaced = Path.Combine(parent, $"{data.Name}.pre-restore-{GameFiles.Stamp(clock.GetUtcNow())}");

		try
		{
			var extracted = await TreeArchive.ExtractAsync(source.Value, staging, ct).ConfigureAwait(false);
			if (!extracted.IsSuccess)
			{
				return Result.Fail<BackupName>(extracted.Failure!);
			}

			var restored = Path.Combine(staging, data.Name);
			if (!Directory.Exists(restored))
			{
				return Failure.Invalid("В резервной копии нет каталога данных");
			}

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

			Directory.Delete(displaced, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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
		CancellationToken ct)
	{
		var source = await SelectAsync(server, backup).ConfigureAwait(false);
		if (!source.IsSuccess)
		{
			return Result.Fail<WorldFile>(source.Failure!);
		}

		var contents = await TreeArchive.InspectAsync(source.Value, ct).ConfigureAwait(false);
		if (!contents.IsSuccess)
		{
			return Result.Fail<WorldFile>(contents.Failure!);
		}

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
