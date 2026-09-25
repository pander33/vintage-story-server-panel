using System.Formats.Tar;
using System.IO.Compression;
using Panel.Content.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Content;

/// <summary>
/// Резервная копия каталога: запись, сверка и распаковка <c>.tar.gz</c>.
/// </summary>
/// <remarks>
/// <para>
/// Реализация ADR-08. В <c>System.Formats.Tar</c> нет аналога <c>filter="data"</c>, у
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
	{
		var judged = await JudgeAsync(archive, ct).ConfigureAwait(false);
		if (!judged.IsSuccess)
		{
			return Result.Fail<int>(judged.Failure!);
		}

		Directory.CreateDirectory(destination);
		var written = 0;

		try
		{
			await foreach (var entry in ReadAsync(archive, ct).ConfigureAwait(false))
			{
				var safe = ArchiveSafety.Judge(Describe(entry));
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
