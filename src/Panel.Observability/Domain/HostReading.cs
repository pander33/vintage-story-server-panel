using System.Globalization;

namespace Panel.Observability.Domain;

/// <summary>
/// Счётчики процессора всей машины с её старта: занятое и всё время, в любых одинаковых единицах.
/// </summary>
/// <remarks>
/// Загрузка — разница двух замеров: сами счётчики говорят только о среднем со старта машины.
/// </remarks>
public readonly record struct CpuTimes(ulong Busy, ulong Total)
{
	/// <summary>
	/// Первая строка <c>/proc/stat</c>: <c>cpu user nice system idle iowait irq softirq steal …</c>.
	/// Простой — <c>idle</c> и <c>iowait</c>; <c>guest</c> уже учтён в <c>user</c> и не складывается.
	/// </summary>
	public static CpuTimes? ParseProcStat(string text)
	{
		ArgumentNullException.ThrowIfNull(text);

		var line = text.AsSpan();
		var end = line.IndexOf('\n');
		if (end >= 0)
		{
			line = line[..end];
		}

		Span<Range> fields = stackalloc Range[12];
		var count = line.Split(fields, ' ', StringSplitOptions.RemoveEmptyEntries);
		if (count < 5 || line[fields[0]] is not "cpu")
		{
			return null;
		}

		ulong total = 0, idle = 0;
		for (var index = 1; index < Math.Min(count, 9); index++)
		{
			if (!ulong.TryParse(line[fields[index]], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
			{
				return null;
			}

			total += value;
			if (index is 4 or 5)
			{
				idle += value;
			}
		}

		return new CpuTimes(total - idle, total);
	}

	/// <summary>Загрузка между двумя замерами, 0–100; без прошедшего времени — неизвестна.</summary>
	public static double? Percent(CpuTimes earlier, CpuTimes later)
	{
		if (later.Total <= earlier.Total || later.Busy < earlier.Busy)
		{
			return null;
		}

		var percent = (double) (later.Busy - earlier.Busy) / (later.Total - earlier.Total) * 100;
		return Math.Round(Math.Clamp(percent, 0, 100), 1);
	}
}

/// <summary>Память машины, байты. Подкачки нет — её размер ноль.</summary>
public readonly record struct HostMemory(long Total, long Available, long SwapTotal, long SwapFree)
{
	public long Used => Math.Max(0, Total - Available);

	public long SwapUsed => Math.Max(0, SwapTotal - SwapFree);

	/// <summary>
	/// <c>/proc/meminfo</c>. Доступная — <c>MemAvailable</c>: свободная плюс кэш, который ядро отдаст
	/// по первому требованию. <c>MemFree</c> на сервере с большим кэшем всегда выглядел бы нехваткой.
	/// </summary>
	public static HostMemory? ParseMeminfo(string text)
	{
		ArgumentNullException.ThrowIfNull(text);

		long total = -1, available = -1, swapTotal = 0, swapFree = 0;
		foreach (var line in text.AsSpan().EnumerateLines())
		{
			var colon = line.IndexOf(':');
			if (colon <= 0)
			{
				continue;
			}

			var value = Kilobytes(line[(colon + 1)..]);
			switch (line[..colon])
			{
				case "MemTotal":
					total = value;
					break;
				case "MemAvailable":
					available = value;
					break;
				case "SwapTotal":
					swapTotal = Math.Max(0, value);
					break;
				case "SwapFree":
					swapFree = Math.Max(0, value);
					break;
			}
		}

		return total > 0 && available >= 0 ? new HostMemory(total, available, swapTotal, swapFree) : null;
	}

	/// <summary><c>   16318412 kB</c> — в байты; нечитаемое значение — -1.</summary>
	private static long Kilobytes(ReadOnlySpan<char> text)
	{
		text = text.Trim();
		if (text.EndsWith("kB", StringComparison.Ordinal))
		{
			text = text[..^2].TrimEnd();
		}

		return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var kilobytes) ? kilobytes * 1024 : -1;
	}
}

/// <summary>Средняя длина очереди процессора за 1, 5 и 15 минут; бывает только на Linux.</summary>
public readonly record struct LoadAverage(double One, double Five, double Fifteen)
{
	/// <summary><c>/proc/loadavg</c>: <c>0.52 0.40 0.35 1/234 5678</c>.</summary>
	public static LoadAverage? ParseLoadavg(string text)
	{
		ArgumentNullException.ThrowIfNull(text);

		var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		return parts.Length >= 3
			   && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var one)
			   && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var five)
			   && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var fifteen)
			? new LoadAverage(one, five, fifteen)
			: null;
	}
}
