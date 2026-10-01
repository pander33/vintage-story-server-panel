using System.Diagnostics;
using System.Globalization;
using Panel.Content.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Content;

/// <summary>
/// Работа с игровыми файлами от имени root: перемещение, атомарная запись, владелец,
/// метки времени.
/// </summary>
/// <remarks>
/// Правила здесь общие: файл игры принадлежит игровому пользователю, запись атомарна,
/// имена в истории не сталкиваются.
///
/// На Windows нет прав Unix и владельца по uid. Доступ задаёт список доступа каталога,
/// который установщик выставляет один раз и который наследуется всем содержимым, поэтому
/// <see cref="SetMode"/> и <see cref="ChangeOwnerAsync"/> там ничего не делают.
/// </remarks>
public static class GameFiles
{
	/// <summary>Учётная запись, от которой работает игра.</summary>
	public const string GameUser = "vsgame";

	/// <summary>Сколько раз пробовать переименовать каталог на Windows (<see cref="MoveDirectoryAsync"/>).</summary>
	private const int moveAttempts = 10;

	/// <summary>Метка времени в именах истории и резервных копий: <c>20260921-143000</c>.</summary>
	public static string Stamp(DateTimeOffset moment) =>
		moment.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

	/// <summary>Метка времени в именах резервных копий: <c>2026-09-21_14-30-00</c>.</summary>
	public static string BackupStamp(DateTimeOffset moment) =>
		moment.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

	/// <summary>
	/// Имя, которого ещё нет в каталоге. При совпадении добавляется второе число: две операции в
	/// одну секунду возможны, и ни одна не должна потеряться.
	/// </summary>
	public static string FreeName(string directory, string stamp, string name)
	{
		var candidate = Path.Combine(directory, $"{stamp}-{name}");

		for (var suffix = 1; Path.Exists(candidate); suffix++)
		{
			candidate = Path.Combine(directory, $"{stamp}-{suffix}-{name}");
		}

		return candidate;
	}

	/// <summary>
	/// Переименование каталога при обмене деревьев (восстановление копии, замена программы).
	/// </summary>
	/// <remarks>
	/// На Windows только что записанные файлы ненадолго открывают антивирус и индексатор, и
	/// переименование каталога получает отказ в доступе, хотя через долю секунды прошло бы.
	/// Поэтому там попытка повторяется с нарастающей паузой, всего около двух секунд, и только
	/// пока источник есть, а назначения нет: иначе ошибка настоящая и ждать нечего. На Linux
	/// переименование либо удаётся сразу, либо не удастся вовсе.
	/// </remarks>
	public static async Task MoveDirectoryAsync(string source, string destination, CancellationToken ct)
	{
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				Directory.Move(source, destination);
				return;
			}
			catch (Exception exception) when (
				exception is IOException or UnauthorizedAccessException
				&& OperatingSystem.IsWindows()
				&& attempt < moveAttempts
				&& Directory.Exists(source)
				&& !Path.Exists(destination))
			{
				await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), ct).ConfigureAwait(false);
			}
		}
	}

	/// <summary>Обычный файл: не ссылка и не каталог. Через ссылку можно выйти за пределы каталога.</summary>
	public static bool IsPlainFile(string path)
	{
		var info = new FileInfo(path);
		return info is { Exists: true, LinkTarget: null };
	}

	/// <summary>
	/// Атомарная замена: запись во временный файл в том же каталоге, затем переименование.
	/// Между файловыми системами переименование не атомарно.
	/// </summary>
	public static async Task<Result<Done>> ReplaceAsync(
		string source,
		string target,
		UnixFileMode mode,
		bool ownedByGame,
		CancellationToken ct)
	{
		var directory = Path.GetDirectoryName(target)!;
		Directory.CreateDirectory(directory);
		var temporary = Path.Combine(directory, $".{Path.GetFileName(target)}.panel-{Environment.ProcessId}");

		try
		{
			File.Copy(source, temporary, overwrite: true);
			SetMode(temporary, mode);

			if (ownedByGame)
			{
				var owned = await ChangeOwnerAsync(temporary, GameUser, ct).ConfigureAwait(false);
				if (!owned.IsSuccess)
				{
					return owned;
				}
			}

			File.Move(temporary, target, overwrite: true);
			return Done.Ok;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Не удалось записать {target}: {exception.Message}");
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
	/// Сменить владельца файла.
	/// </summary>
	/// <remarks>
	/// В BCL смены владельца нет: <c>File.SetUnixFileMode</c> меняет только права. Вызывается
	/// <c>/usr/bin/chown</c>, а не P/Invoke <c>chown(2)</c>: для него пришлось бы переводить имя
	/// в uid через <c>getpwnam_r</c>, структура которого зависит от libc. Аргументы передаются
	/// списком, без оболочки.
	/// </remarks>
	public static Task<Result<Done>> ChangeOwnerAsync(string path, string user, CancellationToken ct) =>
		ChownAsync($"{user}:{user}", path, ct);

	/// <summary>Сменить только группу файла, владелец остаётся прежним.</summary>
	public static Task<Result<Done>> ChangeGroupAsync(string path, string group, CancellationToken ct) =>
		ChownAsync($":{group}", path, ct);

	/// <summary>
	/// Отдать файл владельцу каталога: <c>chown --reference</c>. Так файл, записанный root для
	/// веб-процесса, получает его учётную запись без перевода имени в uid.
	/// </summary>
	public static Task<Result<Done>> ChangeOwnerLikeAsync(string path, string reference, CancellationToken ct) =>
		ChownAsync($"--reference={reference}", path, ct);

	private static async Task<Result<Done>> ChownAsync(string owner, string path, CancellationToken ct)
	{
		if (OperatingSystem.IsWindows())
		{
			return Done.Ok;
		}

		var start = new ProcessStartInfo("/usr/bin/chown")
		{
			RedirectStandardError = true,
			UseShellExecute = false,
		};
		start.ArgumentList.Add(owner);
		start.ArgumentList.Add(path);

		using var process = Process.Start(start);
		if (process is null)
		{
			return Failure.Unavailable("Не удалось запустить chown");
		}

		var error = process.StandardError.ReadToEndAsync(ct);
		await process.WaitForExitAsync(ct).ConfigureAwait(false);

		return process.ExitCode == 0
			? Done.Ok
			: Failure.Unavailable($"Не удалось сменить владельца {path}: {await error.ConfigureAwait(false)}");
	}

	/// <summary>Права Unix. На Windows ничего не делает: доступ там задаёт унаследованный список доступа.</summary>
	public static void SetMode(string path, UnixFileMode mode)
	{
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(path, mode);
		}
	}

	/// <summary>Права Unix; на Windows <see langword="null"/>.</summary>
	public static UnixFileMode? GetMode(string path) =>
		OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path);

	/// <summary>Свободное место в файловой системе каталога.</summary>
	public static long FreeSpace(string directory) => Usage(directory).Free;

	/// <summary>
	/// Размер, занятое и свободное место файловой системы каталога, как <c>shutil.disk_usage</c>.
	/// </summary>
	/// <remarks>
	/// Точка монтирования выбирается по самому длинному совпадающему префиксу пути.
	/// <c>Path.GetPathRoot</c> на Linux всегда даёт <c>/</c>, и копии на отдельном разделе
	/// мерились бы по корневому.
	///
	/// На Windows тома вида <c>C:\</c> сравниваются без учёта регистра.
	/// </remarks>
	public static DiskUsage Usage(string directory)
	{
		try
		{
			var full = Path.GetFullPath(directory);
			var drive = DriveInfo.GetDrives()
				.Where(candidate => IsUnder(full, candidate.Name, OperatingSystem.IsWindows()))
				.OrderByDescending(candidate => candidate.Name.Length)
				.FirstOrDefault(IsReady);

			return drive is null
				? DiskUsage.Unknown
				: new DiskUsage(drive.TotalSize, drive.TotalSize - drive.TotalFreeSpace, drive.AvailableFreeSpace);
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
		{
			return DiskUsage.Unknown;
		}
	}

	/// <summary>Лежит ли путь на томе с этой точкой монтирования.</summary>
	public static bool IsUnder(string path, string mount, bool windows)
	{
		if (windows)
		{
			var volume = mount.EndsWith('\\') ? mount : mount + "\\";
			return path.StartsWith(volume, StringComparison.OrdinalIgnoreCase)
				   || string.Equals(path + "\\", volume, StringComparison.OrdinalIgnoreCase);
		}

		var root = mount.EndsWith('/') ? mount : mount + "/";
		return path == mount || path.StartsWith(root, StringComparison.Ordinal) || mount == "/";
	}

	private static bool IsReady(DriveInfo drive)
	{
		try
		{
			return drive.IsReady;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <summary>
	/// Размер дерева без перехода по символическим ссылкам, с ограничением по времени.
	/// </summary>
	/// <remarks>
	/// В BCL нет номеров устройства и inode, поэтому жёсткие ссылки считаются при каждой
	/// встрече, а граница файловой системы не проверяется. В каталоге миров не бывает ни того,
	/// ни другого.
	/// </remarks>
	public static long DirectorySize(string directory, TimeSpan budget)
	{
		if (!Directory.Exists(directory))
		{
			return 0;
		}

		var deadline = DateTime.UtcNow + budget;
		var total = 0L;
		var pending = new Stack<DirectoryInfo>([new DirectoryInfo(directory)]);
		var visited = 0;

		while (pending.Count > 0)
		{
			var current = pending.Pop();
			IEnumerable<FileSystemInfo> entries;
			try
			{
				entries = current.EnumerateFileSystemInfos();
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				continue;
			}

			foreach (var entry in entries)
			{
				if (++visited % 2048 == 0 && DateTime.UtcNow > deadline)
				{
					return total;
				}

				if (entry.LinkTarget is not null)
				{
					continue;
				}

				switch (entry)
				{
					case DirectoryInfo child:
						pending.Push(child);
						break;
					case FileInfo file:
						total += file.Length;
						break;
				}
			}
		}

		return total;
	}

	/// <summary>Проверка подписи в начале файла, по которой узнаётся база мира.</summary>
	public static async Task<Result<Done>> CheckWorldSignatureAsync(string path, CancellationToken ct)
	{
		try
		{
			var head = new byte[WorldDatabase.SignatureLength];
			await using var file = File.OpenRead(path);
			var read = await file.ReadAsync(head, ct).ConfigureAwait(false);

			return WorldDatabase.CheckSignature(head.AsSpan(0, read));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Invalid($"Не удалось проверить файл мира: {exception.Message}");
		}
	}
}

/// <summary>Место на файловой системе, в байтах.</summary>
public readonly record struct DiskUsage(long Total, long Used, long Free)
{
	public static DiskUsage Unknown => new(0, 0, 0);
}
