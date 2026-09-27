using System.Globalization;
using System.Text;
using Panel.SharedKernel;

namespace Panel.Content.Domain;

/// <summary>Суждение о переносимом архиве сервера до распаковки на любой платформе.</summary>
public static class ServerTransferArchive
{
	public const string Manifest = "vs-panel-server.json";
	public const int MaximumEntries = 100_000;
	public const long MaximumFileBytes = 256L * 1024 * 1024 * 1024;
	public const long MaximumUnpackedBytes = 1024L * 1024 * 1024 * 1024;
	public const int MaximumConfigBytes = 1024 * 1024;

	/// <summary>Проверить, что активный мир из конфигурации действительно находится в пакете.</summary>
	public static Result<Done> RequireActiveWorld(IEnumerable<string> nonemptyWorlds, ServerTransferManifest manifest)
	{
		var name = manifest.ActiveWorld.Split('/').LastOrDefault();
		if (string.IsNullOrEmpty(name) || name is "." or ".." || !name.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase))
		{
			return Failure.Invalid("В пакете сервера не задан активный мир");
		}

		return nonemptyWorlds.Contains("data/Saves/" + name, StringComparer.Ordinal)
			? Done.Ok
			: Failure.Invalid("Активный мир отсутствует в пакете сервера или пуст");
	}

	/// <summary>Проверить весь список записей пакета или старой копии с каталогом data/.</summary>
	public static Result<IReadOnlyList<SafeArchiveEntry>> Judge(IEnumerable<ArchiveEntry> entries)
	{
		var safe = new List<SafeArchiveEntry>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var hasData = false;

		foreach (var entry in entries)
		{
			if (safe.Count >= MaximumEntries)
			{
				return Failure.Invalid("В пакете сервера слишком много записей");
			}

			var judged = JudgeEntry(entry);
			if (!judged.IsSuccess)
			{
				return Result.Fail<IReadOnlyList<SafeArchiveEntry>>(judged.Failure!);
			}

			var item = judged.Value;
			var path = item.RelativePath.Normalize(NormalizationForm.FormC);

			if (!seen.Add(path))
			{
				return Failure.Invalid($"Имена пакета совпадают без учёта регистра: {path}");
			}

			if (item.Kind == ArchiveEntryKind.File)
			{
				files.Add(path);
			}
			else
			{
				directories.Add(path);
			}

			hasData |= path.StartsWith("data/", StringComparison.Ordinal);
			safe.Add(item);
		}

		if (!hasData || !safe.Any(item => item.RelativePath == "data/serverconfig.json" && item.Kind == ArchiveEntryKind.File))
		{
			return Failure.Invalid("В пакете сервера нет data/serverconfig.json");
		}

		foreach (var file in files)
		{
			if (directories.Contains(file))
			{
				return Failure.Invalid($"Файл и каталог пакета совпадают: {file}");
			}

			var parent = file;
			while ((parent = Parent(parent)).Length > 0)
			{
				if (files.Contains(parent))
				{
					return Failure.Invalid($"Файл пакета является родителем другой записи: {parent}");
				}
			}
		}

		foreach (var directory in directories)
		{
			var parent = directory;
			while ((parent = Parent(parent)).Length > 0)
			{
				if (files.Contains(parent))
				{
					return Failure.Invalid($"Файл пакета является родителем каталога: {parent}");
				}
			}
		}

		return safe;
	}

	/// <summary>Проверить размер файла до чтения его содержимого.</summary>
	public static Result<long> CountBytes(long total, long length)
	{
		if (length < 0 || length > MaximumFileBytes || total < 0 || total > MaximumUnpackedBytes - length)
		{
			return Failure.Invalid("Размер содержимого пакета сервера превышает допустимый предел");
		}

		return total + length;
	}

	/// <summary>Повторная проверка записи непосредственно перед записью от имени демона.</summary>
	public static Result<SafeArchiveEntry> JudgeEntry(ArchiveEntry entry)
	{
		if (entry.Name.Contains('\\') || entry.Name.StartsWith('/') || entry.Name.Contains("//", StringComparison.Ordinal))
		{
			return Failure.Invalid($"Недопустимый разделитель в имени пакета: {entry.Name}");
		}

		var judged = ArchiveSafety.Judge(entry);
		if (!judged.IsSuccess)
		{
			return judged;
		}

		var item = judged.Value;
		var path = item.RelativePath;
		if (!path.IsNormalized(NormalizationForm.FormC) || path.Any(character =>
			CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
		{
			return Failure.Invalid($"Имя пакета не приведено к Unicode NFC: {path}");
		}
		if (item.Kind is not (ArchiveEntryKind.File or ArchiveEntryKind.Directory))
		{
			return Failure.Invalid($"Ссылки в пакете сервера запрещены: {path}");
		}

		if (path != Manifest && path != "data" && !path.StartsWith("data/", StringComparison.Ordinal))
		{
			return Failure.Invalid($"Лишний путь в пакете сервера: {path}");
		}

		if (path == Manifest && item.Kind != ArchiveEntryKind.File || path == "data" && item.Kind != ArchiveEntryKind.Directory)
		{
			return Failure.Invalid($"Неверный тип записи пакета: {path}");
		}

		var parts = path.Split('/');
		if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')
			|| part.Any(char.IsControl) || part.IndexOfAny(":*?\"<>|".ToCharArray()) >= 0 || Reserved(part)))
		{
			return Failure.Invalid($"Имя несовместимо с Windows: {path}");
		}

		if (path.Length > 240)
		{
			return Failure.Invalid($"Путь пакета слишком длинный для Windows: {path}");
		}

		return item;
	}

	private static string Parent(string path)
	{
		var slash = path.LastIndexOf('/');
		return slash < 0 ? string.Empty : path[..slash];
	}

	private static bool Reserved(string part)
	{
		var stem = part.Split('.')[0];
		if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
			|| stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
			|| stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
			|| stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		return stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
			|| stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9';
	}
}
