using System.Diagnostics;

namespace Panel.Cli;

/// <summary>Итог внешней команды.</summary>
internal sealed record ShellResult(int ExitCode, string Output, string Error)
{
	public bool Succeeded => ExitCode == 0;

	/// <summary>Первая непустая строка ответа для сообщений об отказе.</summary>
	public string Reason => (Error.Trim() is { Length: > 0 } error ? error : Output.Trim()) is { Length: > 0 } text
		? text.Split('\n')[0]
		: $"exit code {ExitCode}";
}

/// <summary>
/// Запуск программ установщиком. Аргументы передаются списком, без оболочки.
/// </summary>
internal static class Shell
{
	/// <summary>
	/// Выполнить и собрать вывод. Если программа не найдена, код 127, как у оболочки.
	/// <paramref name="echo"/> выводит строки по мере появления, иначе долгая команда (создание
	/// сервера занимает минуты) выглядит зависшей.
	/// </summary>
	public static async Task<ShellResult> RunAsync(
		string file,
		IEnumerable<string> arguments,
		TimeSpan? timeout = null,
		IReadOnlyDictionary<string, string>? environment = null,
		TextWriter? echo = null)
	{
		var start = Prepare(file, arguments, environment);
		start.RedirectStandardOutput = true;
		start.RedirectStandardError = true;
		start.RedirectStandardInput = true;

		using var process = new Process { StartInfo = start };
		try
		{
			process.Start();
		}
		catch (System.ComponentModel.Win32Exception exception)
		{
			return new ShellResult(127, string.Empty, $"{file}: {exception.Message}");
		}

		process.StandardInput.Close();
		var output = CollectAsync(process.StandardOutput, echo);
		var error = CollectAsync(process.StandardError, echo is null ? null : Console.Error);
		using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(10));
		try
		{
			await process.WaitForExitAsync(cancel.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			process.Kill(entireProcessTree: true);
			return new ShellResult(124, await output.ConfigureAwait(false), $"{file}: did not finish within the allotted time");
		}

		return new ShellResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
	}

	private static async Task<string> CollectAsync(StreamReader reader, TextWriter? echo)
	{
		if (echo is null)
		{
			return await reader.ReadToEndAsync().ConfigureAwait(false);
		}

		var collected = new System.Text.StringBuilder();
		while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
		{
			collected.Append(line).Append('\n');
			await echo.WriteLineAsync(line).ConfigureAwait(false);
			await echo.FlushAsync().ConfigureAwait(false);
		}

		return collected.ToString();
	}

	/// <summary>Выполнить с терминалом пользователя: ввод пароля, <c>journalctl -f</c>, долгий вывод.</summary>
	public static async Task<int> RunAttachedAsync(
		string file,
		IEnumerable<string> arguments,
		IReadOnlyDictionary<string, string>? environment = null)
	{
		using var process = new Process { StartInfo = Prepare(file, arguments, environment) };
		try
		{
			process.Start();
		}
		catch (System.ComponentModel.Win32Exception exception)
		{
			await Console.Error.WriteLineAsync($"{file}: {exception.Message}").ConfigureAwait(false);
			return 127;
		}

		await process.WaitForExitAsync().ConfigureAwait(false);
		return process.ExitCode;
	}

	/// <summary>Есть ли программа в PATH.</summary>
	public static bool Exists(string command) =>
		(Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
			.Any(directory => File.Exists(Path.Combine(directory, command)));

	private static ProcessStartInfo Prepare(string file, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment)
	{
		var start = new ProcessStartInfo(file) { UseShellExecute = false };
		foreach (var argument in arguments)
		{
			start.ArgumentList.Add(argument);
		}

		foreach (var (key, value) in environment ?? new Dictionary<string, string>())
		{
			start.Environment[key] = value;
		}

		return start;
	}
}
