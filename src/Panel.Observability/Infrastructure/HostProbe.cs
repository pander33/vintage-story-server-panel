using System.Runtime.InteropServices;
using Panel.Observability.Domain;

namespace Panel.Observability.Infrastructure;

/// <summary>
/// Состояние машины, на которой стоит панель: процессор, память, тома, система.
/// </summary>
/// <remarks>
/// <para>
/// Читает панель, а не демон: на Linux <c>/proc/stat</c>, <c>/proc/meminfo</c> и
/// <c>/proc/loadavg</c> открыты всем, на Windows <c>GetSystemTimes</c> и
/// <c>GlobalMemoryStatusEx</c> не требуют прав. Лишняя операция привилегированной стороне не нужна.
/// </para>
/// <para>
/// Загрузка процессора — разница с прошлым замером. Если его нет или он старше двух минут,
/// снимается новый и полсекунды ожидания: иначе первое открытие «Обзора» показывало бы среднее за
/// неизвестный срок. Запросы чаще раза в секунду получают прошлый ответ.
/// </para>
/// </remarks>
public sealed class HostProbe
{
	private static readonly TimeSpan baseline = TimeSpan.FromMilliseconds(500);
	private static readonly TimeSpan stale = TimeSpan.FromMinutes(2);
	private static readonly TimeSpan reuse = TimeSpan.FromSeconds(1);

	private readonly TimeProvider clock;
	private readonly SemaphoreSlim gate = new(1, 1);
	private readonly Lazy<string> platform;
	private (long At, CpuTimes Times)? previous;
	private double? lastPercent;

	public HostProbe(TimeProvider clock)
	{
		ArgumentNullException.ThrowIfNull(clock);
		this.clock = clock;
		platform = new Lazy<string>(ReadPlatform);
	}

	/// <summary>Имя и версия системы: <c>PRETTY_NAME</c> из <c>/etc/os-release</c> или описание среды.</summary>
	public string Platform => platform.Value;

	/// <summary>Загрузка всех ядер, 0–100; <see langword="null"/>, если счётчики не читаются.</summary>
	public async Task<double?> CpuPercentAsync(CancellationToken ct)
	{
		await gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			var now = clock.GetTimestamp();
			if (previous is { } recent && clock.GetElapsedTime(recent.At, now) < reuse)
			{
				return lastPercent;
			}

			if (previous is not { } earlier || clock.GetElapsedTime(earlier.At, now) > stale)
			{
				if (Cpu() is not { } first)
				{
					return null;
				}

				await Task.Delay(baseline, clock, ct).ConfigureAwait(false);
				earlier = (clock.GetTimestamp(), first);
			}

			if (Cpu() is not { } current)
			{
				return null;
			}

			previous = (clock.GetTimestamp(), current);
			lastPercent = CpuTimes.Percent(earlier.Times, current);
			return lastPercent;
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>Память и подкачка; <see langword="null"/>, если не читаются.</summary>
	public static HostMemory? Memory()
	{
		if (OperatingSystem.IsWindows())
		{
			var status = new MemoryStatus { Length = (uint) Marshal.SizeOf<MemoryStatus>() };

			// Подкачку Windows не показывает отдельно: файл подкачки входит в предел выделения, и
			// разница с памятью была бы оценкой, а не замером.
			return GlobalMemoryStatusEx(ref status)
				? new HostMemory((long) status.TotalPhysical, (long) status.AvailablePhysical, 0, 0)
				: null;
		}

		return Read("/proc/meminfo") is { } text ? HostMemory.ParseMeminfo(text) : null;
	}

	/// <summary>Средняя очередь процессора; на Windows её нет.</summary>
	public static LoadAverage? Load() =>
		OperatingSystem.IsLinux() && Read("/proc/loadavg") is { } text ? LoadAverage.ParseLoadavg(text) : null;

	/// <summary>
	/// Том, на котором лежит путь: точка монтирования с самым длинным совпадающим префиксом, как в
	/// <c>GameFiles.Usage</c> демона. <c>Path.GetPathRoot</c> на Linux всегда дал бы <c>/</c>.
	/// </summary>
	public static VolumeSpace? VolumeOf(string path, IReadOnlyList<DriveInfo> drives)
	{
		ArgumentNullException.ThrowIfNull(drives);
		if (string.IsNullOrWhiteSpace(path))
		{
			return null;
		}

		try
		{
			var full = Path.GetFullPath(path);
			var windows = OperatingSystem.IsWindows();
			var drive = drives
				.Where(candidate => IsUnder(full, candidate.Name, windows))
				.OrderByDescending(candidate => candidate.Name.Length)
				.FirstOrDefault(IsReady);

			return drive is null ? null : new VolumeSpace(drive.Name, drive.TotalSize, drive.AvailableFreeSpace);
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>Лежит ли путь на томе с этой точкой монтирования.</summary>
	public static bool IsUnder(string path, string mount, bool windows)
	{
		ArgumentNullException.ThrowIfNull(path);
		ArgumentNullException.ThrowIfNull(mount);

		if (windows)
		{
			var volume = mount.EndsWith('\\') ? mount : mount + "\\";
			return path.StartsWith(volume, StringComparison.OrdinalIgnoreCase)
				   || string.Equals(path + "\\", volume, StringComparison.OrdinalIgnoreCase);
		}

		var root = mount.EndsWith('/') ? mount : mount + "/";
		return path == mount || path.StartsWith(root, StringComparison.Ordinal) || mount == "/";
	}

	private static CpuTimes? Cpu()
	{
		if (OperatingSystem.IsWindows())
		{
			// Время ядра включает простой, поэтому занятое — ядро и пользователь без простоя.
			return GetSystemTimes(out var idle, out var kernel, out var user)
				? new CpuTimes((ulong) (kernel + user - idle), (ulong) (kernel + user))
				: null;
		}

		return Read("/proc/stat") is { } text ? CpuTimes.ParseProcStat(text) : null;
	}

	private static string ReadPlatform()
	{
		if (OperatingSystem.IsLinux() && Read("/etc/os-release") is { } release)
		{
			foreach (var line in release.Split('\n'))
			{
				if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
				{
					var name = line["PRETTY_NAME=".Length..].Trim().Trim('"');
					if (name.Length > 0)
					{
						return name;
					}
				}
			}
		}

		return RuntimeInformation.OSDescription;
	}

	private static string? Read(string path)
	{
		try
		{
			return File.ReadAllText(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
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

	[StructLayout(LayoutKind.Sequential)]
	private struct MemoryStatus
	{
		public uint Length;
		public uint MemoryLoad;
		public ulong TotalPhysical;
		public ulong AvailablePhysical;
		public ulong TotalPageFile;
		public ulong AvailablePageFile;
		public ulong TotalVirtual;
		public ulong AvailableVirtual;
		public ulong AvailableExtendedVirtual;
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}

/// <summary>Том: точка монтирования, размер и свободное для обычного пользователя место, байты.</summary>
public readonly record struct VolumeSpace(string Mount, long Total, long Free);
