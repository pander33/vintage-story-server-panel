using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Panel.Configuration.Domain;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Content.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Content;

/// <summary>
/// Резервная копия каталога: запись, сверка и распаковка <c>.tar.gz</c>.
/// </summary>
/// <remarks>
/// <para>
/// Проверка безопасности архива до распаковки. В <c>System.Formats.Tar</c> нет аналога <c>filter="data"</c>, у
/// <c>TarFile.ExtractToDirectory</c> есть только <c>overwriteFiles</c>. Поэтому файлы
/// пишутся здесь по одному, и каждую запись сначала проверяет <see cref="ArchiveSafety"/>.
/// </para>
/// <para>
/// Сначала проверяется весь архив, потом начинается запись, чтобы не остановиться на
/// середине распаковки.
/// </para>
/// </remarks>
public static class TreeArchive
{
	/// <summary>
	/// Упаковать каталог. Имена записей строятся относительно родителя, поэтому каталог лежит в
	/// архиве под своим именем и распаковывается на то же место.
	/// </summary>
	public static async Task<Result<int>> WriteAsync(string source, string archive, CancellationToken ct)
	{
		var root = new DirectoryInfo(source);
		if (!root.Exists)
		{
			return Failure.NotFound($"Нечего копировать: нет каталога {source}");
		}

		var baseDirectory = root.Parent?.FullName ?? root.FullName;
		var written = 0;

		try
		{
			await using var file = File.Create(archive);
			await using var gzip = new GZipStream(file, CompressionLevel.Optimal);
			await using var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);

			foreach (var path in Walk(root))
			{
				ct.ThrowIfCancellationRequested();

				var name = Path.GetRelativePath(baseDirectory, path).Replace('\\', '/');
				await writer.WriteEntryAsync(path, name, ct).ConfigureAwait(false);
				written++;
			}
		}
		catch (UnauthorizedAccessException exception)
		{
			// Файл занят работающим сервером. Без него копия не содержала бы тех данных, ради которых
			// её снимают.
			return Failure.Conflict(
				$"Файл недоступен для чтения: {exception.Message}. Остановите сервер и повторите.");
		}
		catch (IOException exception)
		{
			return Failure.Unavailable($"Не удалось записать архив: {exception.Message}");
		}

		return written;
	}

	/// <summary>
	/// Прочитать архив целиком без распаковки, чтобы обрезанный архив обнаружился сейчас,
	/// а не при восстановлении.
	/// </summary>
	public static async Task<Result<BackupContents>> InspectAsync(string archive, CancellationToken ct)
	{
		var files = 0;
		long unpacked = 0;
		var worlds = new List<BackupWorld>();

		try
		{
			await foreach (var entry in ReadAsync(archive, ct).ConfigureAwait(false))
			{
				files++;
				unpacked += entry.Length;

				if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile
					&& entry.Name.EndsWith(WorldFileName.Extension, StringComparison.OrdinalIgnoreCase))
				{
					var name = entry.Name.Replace('\\', '/');
					worlds.Add(new BackupWorld(name[(name.LastIndexOf('/') + 1)..], name, entry.Length));
				}
			}
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException)
		{
			return Failure.Invalid($"Архив повреждён: {exception.Message}");
		}

		return new BackupContents(files, unpacked, worlds);
	}

	/// <summary>
	/// Распаковать архив в каталог. До первой записи на диск проверяется весь архив.
	/// </summary>
	public static async Task<Result<int>> ExtractAsync(string archive, string destination, CancellationToken ct)
		=> await ExtractAsync(archive, destination, transfer: false, ct).ConfigureAwait(false);

	/// <summary>Распаковать пакет сервера или старую копию после проверки совместимости платформ.</summary>
	public static Task<Result<int>> ExtractTransferAsync(string archive, string destination, CancellationToken ct) =>
		ExtractAsync(archive, destination, transfer: true, ct);

	private static async Task<Result<int>> ExtractAsync(string archive, string destination, bool transfer, CancellationToken ct)
	{
		if (transfer && (File.Exists(destination) || Directory.Exists(destination)))
		{
			return Failure.Conflict("Каталог назначения пакета уже существует");
		}

		var judged = transfer
			? await JudgeTransferAsync(archive, ct).ConfigureAwait(false)
			: await JudgeAsync(archive, ct).ConfigureAwait(false);
		if (!judged.IsSuccess)
		{
			return Result.Fail<int>(judged.Failure!);
		}

		try
		{
			Directory.CreateDirectory(destination);
		}
		catch (IOException exception)
		{
			return Failure.Unavailable($"Не удалось создать каталог назначения пакета: {exception.Message}");
		}
		var written = 0;
		long unpacked = 0;

		try
		{
			await foreach (var entry in ReadAsync(archive, ct).ConfigureAwait(false))
			{
				if (transfer)
				{
					if (written >= ServerTransferArchive.MaximumEntries)
					{
						return Failure.Invalid("В пакете сервера слишком много записей");
					}

					var counted = ServerTransferArchive.CountBytes(unpacked, entry.Length);
					if (!counted.IsSuccess)
					{
						return Result.Fail<int>(counted.Failure!);
					}

					unpacked = counted.Value;
				}

				var safe = transfer
					? ServerTransferArchive.JudgeEntry(Describe(entry))
					: ArchiveSafety.Judge(Describe(entry));
				if (!safe.IsSuccess)
				{
					// Не должно случаться: архив уже прошёл JudgeAsync. Проверка оставлена, потому что файлы
					// пишутся от root.
					return Result.Fail<int>(safe.Failure!);
				}
				await WriteEntryAsync(entry, safe.Value, destination, ct).ConfigureAwait(false);
				written++;
			}
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Не удалось распаковать архив: {exception.Message}");
		}

		return written;
	}

	/// <summary>Проверить и дочитать архив переноса без записи на диск.</summary>
	public static Task<Result<Done>> JudgeTransferAsync(string archive, CancellationToken ct) =>
		JudgeTransferAsync(archive, null, ct);

	/// <summary>Проверить пакет и вернуть его опознание за один проход по архиву.</summary>
	public static async Task<Result<TransferIdentity>> InspectTransferAsync(string archive, CancellationToken ct)
	{
		var identity = new TransferIdentityBox();
		var judged = await JudgeTransferAsync(archive, identity, ct).ConfigureAwait(false);
		return judged.IsSuccess ? identity.Value! : Result.Fail<TransferIdentity>(judged.Failure!);
	}

	private sealed class TransferIdentityBox
	{
		public TransferIdentity? Value { get; set; }
	}

	private static async Task<Result<Done>> JudgeTransferAsync(string archive, TransferIdentityBox? sink, CancellationToken ct)
	{
		var manifestSha256 = string.Empty;
		var entries = new List<ArchiveEntry>();
		var nonemptyWorlds = new List<string>();
		var actualFiles = new List<TransferFile>();
		ServerTransferManifest? manifest = null;
		long unpacked = 0;
		string? activeWorld = null;
		try
		{
			var integrity = await CheckGzipAsync(archive, ct).ConfigureAwait(false);
			if (!integrity.IsSuccess)
			{
				return integrity;
			}

			await foreach (var entry in ReadAsync(archive, ct).ConfigureAwait(false))
			{
				if (entries.Count >= ServerTransferArchive.MaximumEntries)
				{
					return Failure.Invalid("В пакете сервера слишком много записей");
				}

				var counted = ServerTransferArchive.CountBytes(unpacked, entry.Length);
				if (!counted.IsSuccess)
				{
					return Result.Fail<Done>(counted.Failure!);
				}

				unpacked = counted.Value;
				entries.Add(Describe(entry));
				if (entry.Name.StartsWith("data/Saves/", StringComparison.Ordinal)
					&& entry.Name.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase) && entry.Length > 0)
				{
					nonemptyWorlds.Add(entry.Name);
				}
				if (entry.DataStream is { } data)
				{
					if (entry.Name == ServerTransferArchive.Manifest)
					{
						if (entry.Length > ServerTransferManifest.MaximumManifestBytes)
						{
							return Failure.Invalid("Манифест сервера слишком велик");
						}
						var bytes = new byte[(int) entry.Length];
						await data.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
						var parsed = ServerTransferManifest.Parse(bytes);
						if (!parsed.IsSuccess)
						{
							return Result.Fail<Done>(parsed.Failure!);
						}
						manifest = parsed.Value;
						manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
						continue;
					}

					if (entry.Name == "data/serverconfig.json")
					{
						if (entry.Length > ServerTransferArchive.MaximumConfigBytes)
						{
							return Failure.Invalid("Настройки сервера в пакете слишком велики");
						}

						var bytes = new byte[(int) entry.Length];
						await data.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
						var config = JsonNode.Parse(bytes) as JsonObject;
						var allowed = ConfigSchema.Fields
							.Where(field => field.Kind is ConfigFieldKind.Boolean or ConfigFieldKind.Integer or ConfigFieldKind.Number or ConfigFieldKind.Select)
							.Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
						if (config is null || config.Any(field => field.Key is not "WorldConfig" and not "ModPaths" &&
							(!allowed.Contains(field.Key) || field.Value is not JsonValue scalar ||
								scalar.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number)))
							|| config["WorldConfig"] is not JsonObject world
							|| world.Any(field => field.Key != "SaveFileLocation")
							|| config["ModPaths"] is not JsonArray modPaths || modPaths.Count != 1
							|| (string?) modPaths[0] != "Mods")
						{
							return Failure.Invalid("Настройки пакета содержат недопустимые или закрытые поля");
						}
						activeWorld = (string?) (config?["WorldConfig"] as JsonObject)?["SaveFileLocation"];
						actualFiles.Add(new TransferFile(entry.Name, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))));
						continue;
					}

					var buffer = new byte[64 * 1024];
					using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
					long actual = 0;
					int read;
					while ((read = await data.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
					{
						actual += read;
						hash.AppendData(buffer.AsSpan(0, read));
						if (actual > entry.Length)
						{
							return Failure.Invalid("Размер файла в пакете не совпадает с заголовком");
						}
					}

					if (actual != entry.Length)
					{
						return Failure.Invalid("Файл в пакете оборван");
					}
					actualFiles.Add(new TransferFile(entry.Name, actual, Convert.ToHexStringLower(hash.GetHashAndReset())));
				}
			}
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException or JsonException or InvalidOperationException)
		{
			return Failure.Invalid($"Архив повреждён: {exception.Message}");
		}

		var judged = ServerTransferArchive.Judge(entries);
		if (!judged.IsSuccess)
		{
			return Result.Fail<Done>(judged.Failure!);
		}
		foreach (var entry in entries)
		{
			if (entry.Kind == ArchiveEntryKind.File && entry.Name is not (ServerTransferArchive.Manifest or "data/serverconfig.json")
				&& !entry.Name.StartsWith("data/Saves/", StringComparison.Ordinal)
				&& !entry.Name.StartsWith("data/Mods/", StringComparison.Ordinal))
			{
				return Failure.Invalid("Пакет содержит файл вне разрешённых каталогов");
			}
			if (entry.Kind == ArchiveEntryKind.File && entry.Name.StartsWith("data/Saves/", StringComparison.Ordinal)
				&& (entry.Name != manifest?.ActiveWorld || !entry.Name.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase))
				|| entry.Kind == ArchiveEntryKind.File && entry.Name.StartsWith("data/Mods/", StringComparison.Ordinal)
				&& (!entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
					&& !entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
					|| entry.Name["data/Mods/".Length..].Contains('/')))
			{
				return Failure.Invalid("Пакет содержит неожиданный файл мира или мода");
			}
		}
		if (manifest is null || !entries.Any(entry => entry.Name == ServerTransferArchive.Manifest))
		{
			return Failure.Invalid("В пакете сервера нет манифеста");
		}
		var verified = manifest.Verify(actualFiles);
		if (!verified.IsSuccess)
		{
			return verified;
		}
		if (manifest.ActiveWorld != "data/" + activeWorld?.Replace('\\', '/'))
		{
			return Failure.Invalid("Активный мир пакета не совпадает с настройками сервера");
		}

		var required = ServerTransferArchive.RequireActiveWorld(nonemptyWorlds, manifest);
		if (required.IsSuccess && sink is not null)
		{
			sink.Value = new TransferIdentity(manifest.ActiveWorld, manifestSha256, manifest.GameVersion);
		}

		return required;
	}

	private static async Task<Result<Done>> CheckGzipAsync(string archive, CancellationToken ct)
	{
		await using var file = File.OpenRead(archive);
		if (file.Length < 18)
		{
			return Failure.Invalid("Архив gzip оборван");
		}

		var header = new byte[2];
		await file.ReadExactlyAsync(header, ct).ConfigureAwait(false);
		if (header[0] != 0x1f || header[1] != 0x8b)
		{
			return Failure.Invalid("Архив не является gzip");
		}

		file.Position = 0;
		// GZipStream сам сверяет CRC и длину в конце потока и бросает InvalidDataException.
		await using var gzip = new GZipStream(file, CompressionMode.Decompress, leaveOpen: true);
		var buffer = new byte[64 * 1024];
		long expanded = 0;
		int read;
		while ((read = await gzip.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
		{
			if (expanded > ServerTransferArchive.MaximumUnpackedBytes - read - ServerTransferArchive.MaximumEntries * 1024L)
			{
				return Failure.Invalid("Размер содержимого пакета сервера превышает допустимый предел");
			}

			expanded += read;
		}

		return Done.Ok;
	}

	/// <summary>
	/// Достать из архива один файл по имени записи. Так восстанавливается отдельный мир без
	/// распаковки всей копии.
	/// </summary>
	public static async Task<Result<long>> ExtractOneAsync(
		string archive,
		string member,
		string destination,
		CancellationToken ct)
	{
		try
		{
			await foreach (var entry in ReadAsync(archive, ct).ConfigureAwait(false))
			{
				if (entry.Name.Replace('\\', '/') != member || entry.DataStream is not { } data)
				{
					continue;
				}

				var safe = ArchiveSafety.Judge(Describe(entry));
				if (!safe.IsSuccess)
				{
					return Result.Fail<long>(safe.Failure!);
				}

				await using var target = File.Create(destination);
				await data.CopyToAsync(target, ct).ConfigureAwait(false);
				return target.Length;
			}
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException)
		{
			return Failure.Invalid($"Архив повреждён: {exception.Message}");
		}

		return Failure.NotFound("В резервной копии нет такой записи");
	}

	/// <summary>
	/// Перечень записей для проверки раскладки пакета сервера. Архив читается до конца:
	/// обрезанный пакет обнаруживается здесь, до остановки сервера.
	/// </summary>
	public static async Task<Result<IReadOnlyList<PackageEntry>>> ListAsync(string archive, CancellationToken ct)
	{
		var entries = new List<PackageEntry>();
		try
		{
			await foreach (var entry in ReadAsync(archive, ct).ConfigureAwait(false))
			{
				entries.Add(new PackageEntry(entry.Name, Describe(entry).Kind, entry.Length));
				if (entries.Count > 100_000)
				{
					// Такой пакет всё равно отвергается по числу записей.
					break;
				}
			}
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException)
		{
			return Failure.Invalid($"Пакет игры не читается: {exception.Message}");
		}

		return entries;
	}

	private static async Task<Result<Done>> JudgeAsync(string archive, CancellationToken ct)
	{
		try
		{
			await foreach (var entry in ReadAsync(archive, ct).ConfigureAwait(false))
			{
				var safe = ArchiveSafety.Judge(Describe(entry));
				if (!safe.IsSuccess)
				{
					return Result.Fail<Done>(safe.Failure!);
				}
			}
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or FormatException)
		{
			return Failure.Invalid($"Архив повреждён: {exception.Message}");
		}

		return Done.Ok;
	}

	private static async IAsyncEnumerable<TarEntry> ReadAsync(
		string archive,
		[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
	{
		await using var file = File.OpenRead(archive);
		await using var gzip = new GZipStream(file, CompressionMode.Decompress);
		await using var reader = new TarReader(gzip);

		while (await reader.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
		{
			yield return entry;
		}
	}

	private static ArchiveEntry Describe(TarEntry entry) => new(
		entry.Name,
		entry.EntryType switch
		{
			TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile
				=> ArchiveEntryKind.File,
			TarEntryType.Directory => ArchiveEntryKind.Directory,
			TarEntryType.SymbolicLink => ArchiveEntryKind.SymbolicLink,
			TarEntryType.HardLink => ArchiveEntryKind.HardLink,
			// Глобальные и расширенные заголовки pax до этого места не доходят:
			// TarReader разбирает их сам и в перечисление не отдаёт.
			_ => ArchiveEntryKind.Other,
		},
		entry.LinkName ?? string.Empty,
		(int) entry.Mode);

	private static async Task WriteEntryAsync(
		TarEntry entry,
		SafeArchiveEntry safe,
		string destination,
		CancellationToken ct)
	{
		var target = Path.Combine(destination, safe.RelativePath.Replace('/', Path.DirectorySeparatorChar));

		switch (safe.Kind)
		{
			case ArchiveEntryKind.Directory:
				Directory.CreateDirectory(target);
				GameFiles.SetMode(target, (UnixFileMode) safe.Mode);
				break;

			case ArchiveEntryKind.File:
				{
					Directory.CreateDirectory(Path.GetDirectoryName(target)!);
					await using var file = File.Create(target);
					if (entry.DataStream is { } data)
					{
						await data.CopyToAsync(file, ct).ConfigureAwait(false);
					}

					GameFiles.SetMode(target, (UnixFileMode) safe.Mode);
					break;
				}

			case ArchiveEntryKind.SymbolicLink:
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				File.CreateSymbolicLink(target, safe.LinkTarget);
				break;

			case ArchiveEntryKind.HardLink:
				// В BCL нельзя создать жёсткую ссылку, а P/Invoke link(2) ради этого не нужен. В игровых
				// каталогах жёстких ссылок нет, копия даёт то же содержимое.
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				var linked = Path.Combine(destination, safe.LinkTarget.Replace('/', Path.DirectorySeparatorChar));
				if (File.Exists(linked))
				{
					File.Copy(linked, target, overwrite: true);
				}

				break;

			default:
				throw new InvalidOperationException($"Неожиданный род записи: {safe.Kind}");
		}
	}

	/// <summary>
	/// Обход каталога без выхода за пределы файловой системы и без хождения по
	/// символическим ссылкам — то же, что <c>tar --one-file-system</c>.
	/// </summary>
	private static IEnumerable<string> Walk(DirectoryInfo root)
	{
		var pending = new Stack<DirectoryInfo>();
		pending.Push(root);

		while (pending.Count > 0)
		{
			var directory = pending.Pop();
			yield return directory.FullName;

			foreach (var entry in directory.EnumerateFileSystemInfos())
			{
				if (entry.LinkTarget is not null)
				{
					// Ссылка попадает в архив как ссылка: если идти по ней, в архив попало бы всё, на что
					// она указывает.
					yield return entry.FullName;
					continue;
				}

				if (entry is DirectoryInfo child)
				{
					pending.Push(child);
				}
				else
				{
					yield return entry.FullName;
				}
			}
		}
	}
}

/// <summary>Опознание проверенного пакета: активный мир, хеш манифеста и версия игры источника.</summary>
public sealed record TransferIdentity(string ActiveWorld, string ManifestSha256, string GameVersion);
