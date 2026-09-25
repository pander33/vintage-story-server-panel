using System.ComponentModel;
using System.Diagnostics;

namespace Panel.Agent.Supervision;

/// <summary>Сведения о процессе, нужные супервизору.</summary>
/// <param name="Created">Момент создания в секундах Unix, как <c>created</c> в файле состояния.</param>
/// <param name="CreatedFileTime">Тот же момент в тиках FILETIME, точно, для сравнения процессов.</param>
public sealed record ProcessSample(double Created, long CreatedFileTime, long WorkingSet, long CpuNanoseconds);

/// <summary>
/// Сведения о процессе по pid. Заменяет <c>WindowsProcess</c> из <c>vs-panel-service</c>,
/// который читал их через ctypes; <see cref="Process"/> использует те же
/// <c>GetProcessTimes</c> и <c>GetProcessMemoryInfo</c>.
/// </summary>
public static class ProcessFacts
{
	/// <summary>Снимок процесса; <see langword="null"/>, если процесса нет или он недоступен.</summary>
	public static ProcessSample? Sample(int pid)
	{
		if (pid <= 0)
		{
			return null;
		}

		try
		{
			using var process = Process.GetProcessById(pid);
			var started = process.StartTime.ToUniversalTime();
			return new ProcessSample(
				new DateTimeOffset(started).ToUnixTimeMilliseconds() / 1000d,
				started.ToFileTimeUtc(),
				process.WorkingSet64,
				process.TotalProcessorTime.Ticks * 100);
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
		{
			// Процесс завершился или недоступен демону.
			return null;
		}
	}

	/// <summary>
	/// Завершить процесс, только если это всё ещё он: пока демон ждал остановки, pid мог
	/// достаться другому процессу.
	/// </summary>
	public static void Kill(SupervisorState state, TimeSpan wait)
	{
		ArgumentNullException.ThrowIfNull(state);

		try
		{
			using var process = Process.GetProcessById(state.Pid);
			var created = new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds() / 1000d;
			if (!state.IsSameProcess(created))
			{
				return;
			}

			process.Kill(entireProcessTree: false);
			process.WaitForExit(wait);
		}
		catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
		{
			// Уже завершился.
		}
	}
}
