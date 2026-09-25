using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Agent.Content;
using Panel.Configuration.Domain;
using Panel.Contracts.Configuration;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Configuration;

/// <summary>
/// <c>serverconfig.json</c> от имени root: чтение, запись с копией, откат.
/// </summary>
/// <remarks>
/// <para>
/// Ревизию, дифф и проверку по схеме делает веб-процесс в контексте Configuration. Демон
/// делает только то, для чего нужны права на игровой каталог: проверяет, что документ —
/// JSON-объект, снимает копию именно этого файла, пишет атомарно и отдаёт файл игровому
/// пользователю.
/// </para>
/// <para>
/// Владелец важен: замена создаёт новый файл, и с правами root:root 0600 игра при следующем
/// старте падает в LoadConfig с EACCES.
/// </para>
/// </remarks>
public sealed class ConfigBackend
{
	/// <summary>Сколько копий показывается и среди скольких ищется откат.</summary>
	private const int listedBackups = 50;

	private const UnixFileMode configMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

	private readonly StagingArea staging;
	private readonly TimeProvider clock;

	public ConfigBackend(StagingArea staging, TimeProvider clock)
	{
		ArgumentNullException.ThrowIfNull(staging);
		ArgumentNullException.ThrowIfNull(clock);

		this.staging = staging;
		this.clock = clock;
	}

	public async Task<Result<ServerConfigFilePayload>> ReadAsync(RegisteredServer server, CancellationToken ct)
	{
		var path = server.ConfigFile;

		try
		{
			if (!GameFiles.IsPlainFile(path))
			{
				return Failure.Unavailable($"Не удалось прочитать настройки сервера: {path} не найден");
			}

			// ReadAllTextAsync сам снимает BOM, как encoding="utf-8-sig".
			var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
			var modified = File.GetLastWriteTimeUtc(path);

			return JsonNode.Parse(json) is JsonObject config
				? new ServerConfigFilePayload
				{
					Config = config,
					ModifiedNanoseconds = (modified - DateTime.UnixEpoch).Ticks * 100,
				}
				: Failure.Unavailable("serverconfig.json не содержит JSON-объекта");
		}
		catch (JsonException exception)
		{
			return Failure.Unavailable($"Не удалось прочитать настройки сервера: {exception.Message}");
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Не удалось прочитать настройки сервера: {exception.Message}");
		}
	}

	public Result<ConfigBackupsPayload> ListBackups(RegisteredServer server) =>
		new ConfigBackupsPayload
		{
			Backups = [.. Backups(server.ConfigFile).Select(file => new ConfigBackupPayload
			{
				Name = file.Name,
				Size = file.Length,
				Modified = UnixSeconds(file.LastWriteTimeUtc),
			})],
		};

	public async Task<Result<ConfigWritePayload>> WriteAsync(
		RegisteredServer server,
		StagedFile staged,
		CancellationToken ct)
	{
		var source = await staging.ResolveAsync(staged, ".json", ct).ConfigureAwait(false);
		if (!source.IsSuccess)
		{
			return Result.Fail<ConfigWritePayload>(source.Failure!);
		}

		var document = await ReadObjectAsync(source.Value, "Настройки сервера должны быть JSON-объектом", ct)
			.ConfigureAwait(false);
		if (!document.IsSuccess)
		{
			return Result.Fail<ConfigWritePayload>(document.Failure!);
		}

		var target = server.ConfigFile;
		var backup = FreeBackupPath(target, avoid: null);

		var copied = CopyAside(target, backup);
		if (!copied.IsSuccess)
		{
			return Result.Fail<ConfigWritePayload>(copied.Failure!);
		}

		var written = await GameFiles.ReplaceAsync(source.Value, target, configMode, ownedByGame: true, ct)
			.ConfigureAwait(false);

		return written.IsSuccess
			? new ConfigWritePayload { Backup = File.Exists(backup) ? backup : null }
			: Result.Fail<ConfigWritePayload>(written.Failure!);
	}

	public async Task<Result<ConfigRollbackPayload>> RollbackAsync(
		RegisteredServer server,
		ConfigBackupName requested,
		CancellationToken ct)
	{
		var target = server.ConfigFile;

		// Имя сверяется ещё раз — теперь с именем файла, которое знает только демон.
		var name = ConfigBackupName.CreateFor(requested.Value, Path.GetFileName(target));
		if (!name.IsSuccess)
		{
			return Result.Fail<ConfigRollbackPayload>(name.Failure!);
		}

		// Откат только к копии из показанного списка: копия, вытесненная из последних пятидесяти,
		// не находится по угаданному имени.
		var source = Backups(target).FirstOrDefault(file => file.Name == name.Value.Value);
		if (source is null)
		{
			return Failure.NotFound("Резервная копия настроек не найдена");
		}

		var document = await ReadObjectAsync(source.FullName, "Резервная копия настроек должна содержать JSON-объект", ct)
			.ConfigureAwait(false);
		if (!document.IsSuccess)
		{
			return Result.Fail<ConfigRollbackPayload>(document.Failure!);
		}

		var safety = FreeBackupPath(target, avoid: source.FullName);
		var copied = CopyAside(target, safety);
		if (!copied.IsSuccess)
		{
			return Result.Fail<ConfigRollbackPayload>(copied.Failure!);
		}

		var written = await GameFiles.ReplaceAsync(source.FullName, target, configMode, ownedByGame: true, ct)
			.ConfigureAwait(false);

		return written.IsSuccess
			? new ConfigRollbackPayload
			{
				Restored = name.Value.Value,
				SafetyBackup = File.Exists(safety) ? Path.GetFileName(safety) : null,
			}
			: Result.Fail<ConfigRollbackPayload>(written.Failure!);
	}

	/// <summary>Копии этого файла: обычные файлы, новые первыми, не больше пятидесяти.</summary>
	private static IReadOnlyList<FileInfo> Backups(string configFile)
	{
		var directory = Path.GetDirectoryName(configFile);
		if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
		{
			return [];
		}

		var prefix = Path.GetFileName(configFile);

		return [.. new DirectoryInfo(directory)
			.EnumerateFiles(prefix + ".panel-*.bak")
			.Where(file => file.LinkTarget is null && ConfigBackupName.CreateFor(file.Name, prefix).IsSuccess)
			.OrderByDescending(file => file.LastWriteTimeUtc)
			.Take(listedBackups)];
	}

	/// <summary>
	/// Свободное имя копии. Если в эту секунду копия уже снята, добавляется число:
	/// <c>…panel-20260921-143000-1.bak</c>.
	/// </summary>
	private string FreeBackupPath(string target, string? avoid)
	{
		var stamp = GameFiles.Stamp(clock.GetUtcNow());
		var candidate = $"{target}.panel-{stamp}.bak";

		for (var suffix = 1; Path.Exists(candidate) || candidate == avoid; suffix++)
		{
			candidate = $"{target}.panel-{stamp}-{suffix}.bak";
		}

		return candidate;
	}

	/// <summary>
	/// Копия текущего файла с сохранением времени изменения: по нему сортируется список копий.
	/// </summary>
	private static Result<Done> CopyAside(string target, string backup)
	{
		if (!File.Exists(target))
		{
			return Done.Ok;
		}

		if (!GameFiles.IsPlainFile(target))
		{
			return Failure.Conflict("Файл настроек сервера не является обычным файлом");
		}

		try
		{
			File.Copy(target, backup, overwrite: false);
			File.SetLastWriteTimeUtc(backup, File.GetLastWriteTimeUtc(target));
			return Done.Ok;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Не удалось сохранить копию настроек: {exception.Message}");
		}
	}

	private static async Task<Result<JsonObject>> ReadObjectAsync(string path, string notObject, CancellationToken ct)
	{
		try
		{
			var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
			return JsonNode.Parse(json) is JsonObject document ? document : Failure.Invalid(notObject);
		}
		catch (JsonException exception)
		{
			return Failure.Invalid($"Некорректный JSON: {exception.Message}");
		}
	}

	private static long UnixSeconds(DateTime utc) => (long) (utc - DateTime.UnixEpoch).TotalSeconds;
}
