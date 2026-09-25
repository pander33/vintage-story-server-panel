using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Panel.Contracts.Agent;
using Panel.SharedKernel;

namespace Panel.Agent.Provisioning;

/// <summary>Итог внешней программы.</summary>
public sealed record HostCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
	/// <summary>Хвост вывода для текста отказа: <c>(stderr or stdout).strip()[-2000:]</c>.</summary>
	public string Tail(string fallback)
	{
		var text = (StandardError.Trim().Length > 0 ? StandardError : StandardOutput).Trim();
		if (text.Length == 0)
		{
			text = fallback;
		}

		return text.Length > 2000 ? text[^2000..] : text;
	}
}

/// <summary>
/// Внешние программы для создания сервера: <c>dotnet</c>, <c>setpriv</c>, <c>useradd</c>,
/// <c>chown</c>, <c>systemctl</c>.
/// </summary>
/// <remarks>
/// <para>
/// Аргументы передаются списком, без оболочки. Пути к программам фиксированы, кроме dotnet:
/// его ищут в известных местах, затем в PATH.
/// </para>
/// <para>
/// Понижение привилегий по ADR-07, вариант B. В .NET нет точки между fork и exec, где можно
/// сделать <c>setgid</c>, <c>setgroups([])</c> и <c>setuid</c>. Вместо этого
/// <c>setpriv --reuid --regid --clear-groups</c>; без <c>setpriv</c> игра не запускается
/// совсем, то есть и от root тоже.
/// </para>
/// <para>
/// Запуск программ, поиск dotnet и опрос сред выполнения нужны на обеих платформах; учётная
/// запись игры, смена владельца и systemctl только на Linux и помечены отдельно.
/// </para>
/// </remarks>
public static partial class HostTools
{
	public const string Systemctl = "/usr/bin/systemctl";

	public const string Setpriv = "/usr/bin/setpriv";

	public const string Journalctl = "/usr/bin/journalctl";

	public const string Useradd = "/usr/sbin/useradd";

	public const string Chown = "/usr/bin/chown";

	/// <summary>Обёртка консоли, которую ставит установщик: создаёт FIFO и запускает сервер через exec.</summary>
	public const string ConsoleWrapper = "/usr/local/libexec/vs-console-wrapper";

	private static readonly string[] dotnetCandidates =
	[
		"/usr/bin/dotnet", "/usr/local/bin/dotnet", "/usr/share/dotnet/dotnet", "/usr/lib/dotnet/dotnet",
	];

	/// <summary>Выполнить программу и дождаться; по таймауту процесс убивается вместе с потомками.</summary>
	public static async Task<HostCommandResult> RunAsync(
		string file,
		IEnumerable<string> arguments,
		TimeSpan timeout,
		CancellationToken ct,
		IReadOnlyDictionary<string, string>? environment = null,
		string? workingDirectory = null)
	{
		var start = new ProcessStartInfo(file)
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		foreach (var argument in arguments)
		{
			start.ArgumentList.Add(argument);
		}

		if (environment is not null)
		{
			// Окружение заменяется целиком, как env= у subprocess.run: игра не должна
			// унаследовать окружение демона, работающего от root.
			start.Environment.Clear();
			foreach (var (key, value) in environment)
			{
				start.Environment[key] = value;
			}
		}

		if (workingDirectory is not null)
		{
			start.WorkingDirectory = workingDirectory;
		}

		using var process = Process.Start(start) ?? throw new IOException($"Не удалось запустить {file}");
		using var limited = CancellationTokenSource.CreateLinkedTokenSource(ct);
		limited.CancelAfter(timeout);

		var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
		var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
		try
		{
			await process.WaitForExitAsync(limited.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			process.Kill(entireProcessTree: true);
			throw new TimeoutException($"{Path.GetFileName(file)} не завершился за {(int) timeout.TotalSeconds} с");
		}

		return new HostCommandResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
	}

	/// <summary>Известные места, затем PATH; только исполняемый обычный файл.</summary>
	public static string? FindDotnet()
	{
		if (OperatingSystem.IsWindows())
		{
			return Supervision.SupervisedBackend.DotnetPath();
		}

		foreach (var candidate in dotnetCandidates)
		{
			if (IsExecutable(candidate))
			{
				return candidate;
			}
		}

		var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
		return path.Split(':', StringSplitOptions.RemoveEmptyEntries)
			.Select(directory => Path.Combine(directory, "dotnet"))
			.FirstOrDefault(IsExecutable);
	}

	/// <summary>На Windows setpriv нет: игру там запускает служба-супервизор.</summary>
	public static string? FindSetpriv() => !OperatingSystem.IsWindows() && IsExecutable(Setpriv) ? Setpriv : null;

	/// <summary>Какой .NET есть на машине.</summary>
	public static async Task<RuntimeProbePayload> ProbeRuntimeAsync(CancellationToken ct)
	{
		var dotnet = FindDotnet();
		var runtimes = new List<DotnetRuntimePayload>();
		if (dotnet is not null)
		{
			var listed = await RunAsync(dotnet, ["--list-runtimes"], TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
			foreach (var line in listed.StandardOutput.Split('\n'))
			{
				var match = RuntimeLine().Match(line.Trim());
				if (match.Success)
				{
					runtimes.Add(new DotnetRuntimePayload
					{
						Name = match.Groups[1].Value,
						Major = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
						Version = $"{match.Groups[2].Value}.{match.Groups[3].Value}.{match.Groups[4].Value}",
					});
				}
			}
		}

		return new RuntimeProbePayload
		{
			Dotnet = dotnet ?? string.Empty,
			Runtimes = runtimes,
			Majors = [.. runtimes.Select(item => item.Major).Distinct().Order()],
			Setpriv = FindSetpriv() ?? string.Empty,
			Platform = OperatingSystem.IsWindows() ? "windows" : "linux",
		};
	}

	/// <summary>
	/// Общая непривилегированная учётная запись игры: найти или создать системную, без домашнего
	/// каталога и оболочки.
	/// </summary>
	[SupportedOSPlatform("linux")]
	public static async Task<Result<Done>> EnsureGameUserAsync(string user, string home, CancellationToken ct)
	{
		if (await HasUserAsync(user, ct).ConfigureAwait(false))
		{
			return Done.Ok;
		}

		if (!File.Exists(Useradd))
		{
			return Failure.Unavailable($"useradd not found; create the {user} system account manually");
		}

		var created = await RunAsync(
				Useradd,
				["--system", "--no-create-home", "--home-dir", home, "--shell", "/usr/sbin/nologin", user],
				TimeSpan.FromSeconds(60),
				ct)
			.ConfigureAwait(false);
		if (created.ExitCode != 0)
		{
			return Failure.Unavailable(created.Tail("useradd failed"));
		}

		return await HasUserAsync(user, ct).ConfigureAwait(false)
			? Done.Ok
			: Failure.Unavailable($"Could not create the {user} account");
	}

	/// <summary>
	/// Отдать дерево игре целиком, включая корень, не переходя по ссылкам: <c>chown -R -h</c>.
	/// Новое дерево из проверенного пакета не содержит ни ссылок, ни точек монтирования.
	/// </summary>
	[SupportedOSPlatform("linux")]
	public static async Task<Result<Done>> ChownTreeAsync(string path, string user, CancellationToken ct)
	{
		var result = await RunAsync(Chown, ["-R", "-h", $"{user}:{user}", path], TimeSpan.FromSeconds(600), ct)
			.ConfigureAwait(false);
		return result.ExitCode == 0 ? Done.Ok : Failure.Unavailable(result.Tail("chown failed"));
	}

	/// <summary>systemctl с фиксированным набором аргументов; ненулевой код — отказ с его выводом.</summary>
	[SupportedOSPlatform("linux")]
	public static async Task<Result<Done>> SystemctlAsync(IEnumerable<string> arguments, TimeSpan timeout, CancellationToken ct)
	{
		var result = await RunAsync(Systemctl, arguments, timeout, ct).ConfigureAwait(false);
		return result.ExitCode == 0 ? Done.Ok : Failure.Unavailable(result.Tail($"systemctl exit {result.ExitCode}"));
	}

	private static async Task<bool> HasUserAsync(string user, CancellationToken ct)
	{
		try
		{
			var lines = await File.ReadAllLinesAsync("/etc/passwd", ct).ConfigureAwait(false);
			return lines.Any(line => line.StartsWith(user + ":", StringComparison.Ordinal));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static bool IsExecutable(string path)
	{
		try
		{
			var info = new FileInfo(path);
			if (!info.Exists)
			{
				return false;
			}

			if (OperatingSystem.IsWindows())
			{
				return true;
			}

			var mode = File.GetUnixFileMode(path);
			return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	[GeneratedRegex(@"\A(Microsoft\.\S+)\s+(\d+)\.(\d+)\.(\d+)", RegexOptions.CultureInvariant)]
	private static partial Regex RuntimeLine();
}
