using Panel.SharedKernel;

namespace Panel.Content.Domain;

/// <summary>Запись пакета сервера для проверки раскладки.</summary>
public sealed record PackageEntry(string Name, ArchiveEntryKind Kind, long Size);

/// <summary>Итог проверки: число записей, размер после распаковки, каталог сервера.</summary>
public sealed record PackageLayout(int Files, long UnpackedSize, string Root);

/// <summary>
/// Раскладка пакета сервера.
/// </summary>
/// <remarks>
/// <para>
/// Правило одно для официального архива и для снимка программы перед обновлением: откат
/// проверяет снимок той же функцией. В снимке пути начинаются с имени каталога программы,
/// поэтому <c>VintagestoryServer.dll</c> лежит на втором уровне, а корнем становится этот
/// каталог.
/// </para>
/// <para>
/// Ссылки, жёсткие ссылки и устройства запрещены совсем, без проверки цели, как в
/// <see cref="ArchiveSafety"/>: в программе сервера их не бывает.
/// </para>
/// </remarks>
public static class ServerPackage
{
	public const string ServerDll = "VintagestoryServer.dll";

	private const int maximumEntries = 100_000;

	private const long maximumUnpacked = 12L * 1024 * 1024 * 1024;

	public static Result<PackageLayout> Judge(IReadOnlyList<PackageEntry> entries)
	{
		ArgumentNullException.ThrowIfNull(entries);

		if (entries.Count is 0 or > maximumEntries)
		{
			return Failure.Invalid("Пакет игры пуст или слишком велик");
		}

		var servers = new List<string[]>();
		long total = 0;

		foreach (var entry in entries)
		{
			var parts = Parts(entry.Name);
			if (parts.Length == 0 || parts.Contains("..") || entry.Name.StartsWith('/')
				|| entry.Kind is ArchiveEntryKind.SymbolicLink or ArchiveEntryKind.HardLink or ArchiveEntryKind.Other)
			{
				return Failure.Invalid("В пакете игры есть небезопасный путь");
			}

			total += Math.Max(0, entry.Size);
			if (entry.Kind == ArchiveEntryKind.File && parts[^1] == ServerDll && parts.Length <= 2)
			{
				servers.Add(parts);
			}
		}

		if (servers.Count != 1 || total > maximumUnpacked)
		{
			return Failure.Invalid("В корне пакета должен быть ровно один VintagestoryServer.dll");
		}

		return new PackageLayout(entries.Count, total, servers[0].Length == 2 ? servers[0][0] : string.Empty);
	}

	/// <summary>
	/// <c>PurePosixPath(name).parts</c> без корня: пустые части и «.» отбрасываются, обратная
	/// косая черта считается обычным символом имени.
	/// </summary>
	private static string[] Parts(string name) =>
		name.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(part => part != ".").ToArray();
}
