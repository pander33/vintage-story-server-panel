using System.IO.Compression;
using Panel.Content.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Content;

/// <summary>
/// Пакет программы сервера в одном из двух официальных форматов: <c>.tar.gz</c> для Linux
/// и <c>.zip</c> для Windows.
/// </summary>
/// <remarks>
/// <para>
/// Формат определяется по расширению во временном каталоге, которое веб-процесс взял из
/// манифеста. Содержимое всё равно проверяет <see cref="ServerPackage"/>: ZIP под видом
/// пакета Linux отвергается, как любой пакет без <c>VintagestoryServer.dll</c>.
/// </para>
/// <para>
/// ZIP распаковывается по тем же правилам, что tar: сначала проверка всего архива, потом
/// запись, каждая запись через <see cref="ArchiveSafety"/>. Ссылок в ZIP нет: запись с
/// атрибутами символической ссылки Unix становится обычным файлом с текстом цели. Размер
/// записи при копировании сверяется с объявленным, потому что заголовок может лгать, а
/// проверка раскладки опирается на него.
/// </para>
/// </remarks>
public static class PackageArchive
{
	public const string TarExtension = ".tar.gz";

	public const string ZipExtension = ".zip";

	/// <summary>Расширения, под которыми пакет игры может лежать во временном каталоге.</summary>
	public static IReadOnlyList<string> Extensions { get; } = [TarExtension, ZipExtension];

	/// <summary>Расширение пакета по имени; <see langword="null"/>, если это не пакет.</summary>
	public static string? ExtensionOf(string name) =>
		Extensions.FirstOrDefault(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

	public static async Task<Result<IReadOnlyList<PackageEntry>>> ListAsync(string archive, CancellationToken ct)
	{
		if (!IsZip(archive))
		{
			return await TreeArchive.ListAsync(archive, ct).ConfigureAwait(false);
		}

		try
		{
			using var zip = ZipFile.OpenRead(archive);
			var entries = new List<PackageEntry>(Math.Min(zip.Entries.Count, 100_001));
			foreach (var entry in zip.Entries)
			{
				ct.ThrowIfCancellationRequested();
				var described = Describe(entry);
				entries.Add(new PackageEntry(described.Name, described.Kind, entry.Length));
				if (entries.Count > 100_000)
				{
					break;
				}
			}

			return entries;
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException)
		{
			return Failure.Invalid($"Пакет игры не читается: {exception.Message}");
		}
	}

	public static async Task<Result<int>> ExtractAsync(string archive, string destination, CancellationToken ct)
	{
		if (!IsZip(archive))
		{
			return await TreeArchive.ExtractAsync(archive, destination, ct).ConfigureAwait(false);
		}

		try
		{
			using var zip = ZipFile.OpenRead(archive);
			var judged = new List<(ZipArchiveEntry Entry, SafeArchiveEntry Safe)>(zip.Entries.Count);
			foreach (var entry in zip.Entries)
			{
				var safe = ArchiveSafety.Judge(Describe(entry));
				if (!safe.IsSuccess)
				{
					return Result.Fail<int>(safe.Failure!);
				}

				judged.Add((entry, safe.Value));
			}

			Directory.CreateDirectory(destination);
			foreach (var (entry, safe) in judged)
			{
				var target = Path.Combine(destination, safe.RelativePath.Replace('/', Path.DirectorySeparatorChar));
				if (safe.Kind == ArchiveEntryKind.Directory)
				{
					Directory.CreateDirectory(target);
					continue;
				}

				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				await using var source = entry.Open();
				await using var file = File.Create(target);
				await CopyDeclaredAsync(source, file, entry.Length, entry.FullName, ct).ConfigureAwait(false);
			}

			return judged.Count;
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Не удалось распаковать пакет: {exception.Message}");
		}
	}

	private static bool IsZip(string archive) =>
		archive.EndsWith(ZipExtension, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Имя записи с прямыми косыми чертами. Спецификация ZIP требует их, но упаковщики на Windows
	/// иногда пишут обратные, и тогда проверка раскладки не нашла бы сервер на втором уровне.
	/// </summary>
	private static ArchiveEntry Describe(ZipArchiveEntry entry)
	{
		var name = entry.FullName.Replace('\\', '/');
		return name.EndsWith('/')
			? ArchiveEntry.CreateDirectory(name.TrimEnd('/'))
			: ArchiveEntry.CreateFile(name);
	}

	private static async Task CopyDeclaredAsync(Stream source, Stream target, long declared, string name, CancellationToken ct)
	{
		var buffer = new byte[81920];
		long copied = 0;
		int read;
		while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
		{
			copied += read;
			if (copied > declared)
			{
				throw new InvalidDataException($"Запись {name} больше объявленного размера");
			}

			await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
		}
	}
}
