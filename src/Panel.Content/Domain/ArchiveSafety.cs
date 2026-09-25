using Panel.SharedKernel;

namespace Panel.Content.Domain;

/// <summary>Вид записи в архиве. Перечень закрыт, всё остальное отвергается.</summary>
public enum ArchiveEntryKind
{
	/// <summary>Обычный файл.</summary>
	File,

	/// <summary>Каталог.</summary>
	Directory,

	/// <summary>Символическая ссылка: цель разбирается относительно каталога записи.</summary>
	SymbolicLink,

	/// <summary>Жёсткая ссылка: цель разбирается относительно корня архива.</summary>
	HardLink,

	/// <summary>Устройство, сокет, FIFO и прочее, чего не бывает в резервной копии игры.</summary>
	Other,
}

/// <summary>Запись архива для проверки.</summary>
/// <param name="Name">Имя внутри архива, как его записал упаковщик.</param>
/// <param name="Kind">Вид записи.</param>
/// <param name="LinkTarget">Цель ссылки; для остальных записей пустая строка.</param>
/// <param name="Mode">Права в восьмеричном виде из заголовка tar.</param>
public readonly record struct ArchiveEntry(string Name, ArchiveEntryKind Kind, string LinkTarget, int Mode)
{
	public static ArchiveEntry CreateFile(string name, int mode = 0b110_100_100) =>
		new(name, ArchiveEntryKind.File, string.Empty, mode);

	public static ArchiveEntry CreateDirectory(string name, int mode = 0b111_101_101) =>
		new(name, ArchiveEntryKind.Directory, string.Empty, mode);

	public static ArchiveEntry CreateSymbolicLink(string name, string target) =>
		new(name, ArchiveEntryKind.SymbolicLink, target, 0b111_111_111);

	public static ArchiveEntry CreateHardLink(string name, string target) =>
		new(name, ArchiveEntryKind.HardLink, target, 0b110_100_100);
}

/// <summary>
/// Проверенная запись: относительный путь распаковки и безопасные права. Создаётся только
/// <see cref="ArchiveSafety"/>.
/// </summary>
public readonly record struct SafeArchiveEntry
{
	internal SafeArchiveEntry(string relativePath, ArchiveEntryKind kind, string linkTarget, int mode)
	{
		RelativePath = relativePath;
		Kind = kind;
		LinkTarget = linkTarget;
		Mode = mode;
	}

	/// <summary>Путь относительно каталога распаковки, всегда через <c>/</c>.</summary>
	public string RelativePath { get; }

	public ArchiveEntryKind Kind { get; }

	public string LinkTarget { get; }

	/// <summary>Права после снятия setuid, setgid и sticky.</summary>
	public int Mode { get; }
}

/// <summary>
/// Правила фильтра <c>data</c> из <c>tarfile</c>, выписанные явно.
/// </summary>
/// <remarks>
/// <para>
/// В <c>System.Formats.Tar</c> нет аналога <c>filter="data"</c>: у
/// <c>TarFile.ExtractToDirectory</c> есть только <c>overwriteFiles</c>, пути и виды записей он
/// не проверяет. Поэтому по ADR-08 правила написаны здесь, в предметной области, где их можно
/// проверить тестами без настоящего архива.
/// </para>
/// <para>
/// Проверка не обращается к файловой системе. С <c>Path.GetFullPath</c> результат зависел бы от
/// символических ссылок, уже лежащих в целевом каталоге, а проверять нужно содержимое архива.
/// </para>
/// </remarks>
public static class ArchiveSafety
{
	/// <summary>Биты, которые снимаются с каждой записи: setuid, setgid, sticky.</summary>
	private const int dangerousBits = 0b111_000_000_000;

	/// <summary>
	/// Проверить одну запись. Одной опасной записи достаточно, чтобы архив не был распакован
	/// совсем.
	/// </summary>
	public static Result<SafeArchiveEntry> Judge(ArchiveEntry entry)
	{
		var name = Normalize(entry.Name);

		if (name.Length == 0)
		{
			return Failure.Invalid("В архиве есть запись без имени");
		}

		if (IsAbsolute(name) || Escapes(name))
		{
			return Failure.Invalid($"Запись архива выходит за пределы каталога: {entry.Name}");
		}

		if (entry.Kind == ArchiveEntryKind.Other)
		{
			return Failure.Invalid($"Запись архива не является файлом или каталогом: {entry.Name}");
		}

		if (entry.Kind is ArchiveEntryKind.SymbolicLink or ArchiveEntryKind.HardLink)
		{
			var target = Normalize(entry.LinkTarget);

			if (target.Length == 0 || IsAbsolute(target))
			{
				return Failure.Invalid($"Ссылка в архиве указывает наружу: {entry.Name}");
			}

			// Символическая ссылка разбирается относительно своего каталога, жёсткая — относительно
			// корня архива: ссылка a/b -> ../../x выходит наружу, а жёсткая ссылка с той же целью нет.
			var resolved = entry.Kind == ArchiveEntryKind.SymbolicLink
				? Combine(Parent(name), target)
				: target;

			if (Escapes(resolved))
			{
				return Failure.Invalid($"Ссылка в архиве указывает наружу: {entry.Name}");
			}
		}

		return new SafeArchiveEntry(name, entry.Kind, Normalize(entry.LinkTarget), entry.Mode & ~dangerousBits);
	}

	/// <summary>
	/// Проверить весь архив. Разбор останавливается на первом отказе.
	/// </summary>
	public static Result<IReadOnlyList<SafeArchiveEntry>> Judge(IEnumerable<ArchiveEntry> entries)
	{
		var safe = new List<SafeArchiveEntry>();

		foreach (var entry in entries)
		{
			var judged = Judge(entry);

			if (!judged.IsSuccess)
			{
				return Result.Fail<IReadOnlyList<SafeArchiveEntry>>(judged.Failure!);
			}

			safe.Add(judged.Value);
		}

		return Result.Ok<IReadOnlyList<SafeArchiveEntry>>(safe);
	}

	/// <summary>Разделители приводятся к <c>/</c>, завершающий срезается: <c>a/b/</c> и <c>a/b</c> — одно место.</summary>
	private static string Normalize(string value) =>
		value.Replace('\\', '/').TrimEnd('/');

	/// <summary>
	/// Абсолютными считаются и <c>/etc/passwd</c>, и <c>C:/Windows</c>. На Linux второе не
	/// абсолютно, но копии переносятся между машинами, и буква диска в имени записи — признак
	/// подделки.
	/// </summary>
	private static bool IsAbsolute(string name) =>
		name.StartsWith('/') || (name.Length >= 2 && name[1] == ':' && char.IsAsciiLetter(name[0]));

	/// <summary>
	/// Выходит ли путь за пределы корня. Проверяется по сегментам, а не по подстроке <c>..</c>:
	/// имя <c>..hidden</c> допустимо, <c>a/../b</c> остаётся внутри, а <c>a/../../b</c> нет.
	/// </summary>
	private static bool Escapes(string name)
	{
		var depth = 0;

		foreach (var segment in name.Split('/'))
		{
			if (segment is "" or ".")
			{
				continue;
			}

			if (segment == "..")
			{
				if (--depth < 0)
				{
					return true;
				}

				continue;
			}

			depth++;
		}

		return false;
	}

	private static string Parent(string name)
	{
		var slash = name.LastIndexOf('/');
		return slash < 0 ? string.Empty : name[..slash];
	}

	private static string Combine(string directory, string relative) =>
		directory.Length == 0 ? relative : directory + "/" + relative;
}
