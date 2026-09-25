using System.Globalization;
using Panel.Maintenance.Domain;

namespace Panel.Maintenance.Infrastructure;

/// <summary>
/// Текущий процесс и проверка, жив ли другой, по <c>/proc</c>, без
/// <c>System.Diagnostics.Process</c>: работа с процессами разрешена только привилегированной
/// сборке (PrivilegeBoundaryTests).
/// </summary>
/// <remarks>
/// Вне Linux <c>/proc</c> нет. Тогда владелец определяется только по pid текущего процесса, а
/// любой другой считается мёртвым: панель на такой машине одна, и оставшийся файл аренды
/// может остаться только от прошлого запуска.
/// </remarks>
public sealed class ProcessProbe
{
	private readonly string proc;

	public ProcessProbe(string proc = "/proc") => this.proc = proc;

	public LeaseHolder Current()
	{
		var pid = Environment.ProcessId;
		return new LeaseHolder(Boot(), pid, StartTicks(pid) ?? 0);
	}

	public bool IsAlive(LeaseHolder holder)
	{
		if (holder == Current())
		{
			return true;
		}

		return holder.Boot.Length > 0 && holder.Boot == Boot() && StartTicks(holder.Pid) == holder.StartTicks;
	}

	private string Boot()
	{
		try
		{
			return File.ReadAllText(Path.Combine(proc, "sys", "kernel", "random", "boot_id")).Trim();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return string.Empty;
		}
	}

	/// <summary>
	/// Поле 22 <c>/proc/&lt;pid&gt;/stat</c>: момент запуска в тактах от загрузки. Имя процесса в
	/// скобках может содержать пробелы и скобки, поэтому счёт идёт от последней закрывающей
	/// скобки.
	/// </summary>
	private long? StartTicks(int pid)
	{
		try
		{
			var stat = File.ReadAllText(Path.Combine(proc, pid.ToString(CultureInfo.InvariantCulture), "stat"));
			var fields = stat[(stat.LastIndexOf(')') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
			return fields.Length > 19 && long.TryParse(fields[19], CultureInfo.InvariantCulture, out var ticks) ? ticks : null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}
}
