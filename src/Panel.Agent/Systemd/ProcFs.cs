using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Panel.Agent.Systemd;

/// <summary>
/// Данные из /proc для идентичности процесса и времени работы.
/// </summary>
[SupportedOSPlatform("linux")]
public static class ProcFs
{
	private const int scClkTck = 2;

	[DllImport("libc", EntryPoint = "sysconf", SetLastError = true)]
	private static extern long Sysconf(int name);

	/// <summary>
	/// Тиков в секунду (SC_CLK_TCK). Константа 100 верна почти всегда, но в остальных случаях
	/// время старта было бы неверным.
	/// </summary>
	public static long ClockTicksPerSecond()
	{
		var value = Sysconf(scClkTck);
		return value > 0 ? value : 100;
	}

	public static string BootId() =>
		File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();

	/// <summary>Время работы машины в секундах: первое число из /proc/uptime.</summary>
	public static double MachineUptimeSeconds()
	{
		var text = File.ReadAllText("/proc/uptime");
		var space = text.IndexOf(' ', StringComparison.Ordinal);
		var head = space > 0 ? text[..space] : text.Trim();
		return double.Parse(head, System.Globalization.CultureInfo.InvariantCulture);
	}

	/// <summary>
	/// Двадцать второе поле /proc/&lt;pid&gt;/stat: момент старта процесса в тиках. Разбор идёт
	/// от последней закрывающей скобки: второе поле — имя исполняемого файла в скобках, и в нём
	/// могут быть пробелы и скобки.
	/// </summary>
	public static long StartTicks(int pid)
	{
		var stat = File.ReadAllText($"/proc/{pid}/stat");
		var close = stat.LastIndexOf(')');
		if (close < 0)
		{
			throw new FormatException($"Неожиданный формат /proc/{pid}/stat");
		}

		var fields = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

		// После имени процесса поля нумеруются с третьего, поэтому двадцать второе поле — fields[19].
		const int startTimeIndex = 19;
		if (fields.Length <= startTimeIndex)
		{
			throw new FormatException($"В /proc/{pid}/stat не хватает полей");
		}

		return long.Parse(fields[startTimeIndex], System.Globalization.CultureInfo.InvariantCulture);
	}
}
