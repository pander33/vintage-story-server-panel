using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Panel.Agent.Runtime;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Systemd;

/// <summary>
/// Игровой сервер как юнит systemd: запуск, описание, журнал и консоль.
/// </summary>
/// <remarks>
/// Работа с systemd для запущенного сервера собрана здесь. На Windows те же операции
/// протокола реализует <see cref="Supervision.SupervisedBackend"/>.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class SystemdBackend : IServiceBackend
{
	private const string systemctl = "/usr/bin/systemctl";
	private const string journalctl = "/usr/bin/journalctl";
	private const string screen = "/usr/bin/screen";

	private const string statusProperties =
		"ActiveState,SubState,MainPID,ExecMainStartTimestamp,MemoryCurrent,CPUUsageNSec,NRestarts";

	/// <summary>
	/// Сколько ждать перед чтением ответа из журнала.
	/// </summary>
	public TimeSpan ConsoleReadBackDelay { get; init; } = TimeSpan.FromMilliseconds(1200);

	private readonly TimeProvider time;

	public SystemdBackend(TimeProvider? time = null) => this.time = time ?? TimeProvider.System;

	public string Name => "systemd";

	public async Task<ServiceStatusPayload> StatusAsync(RegisteredServer server, CancellationToken ct)
	{
		var output = await RunAsync(systemctl, ["show", server.Unit, $"--property={statusProperties}"], ct)
			.ConfigureAwait(false);
		var values = Properties(output.StandardOutput);

		var pid = Int(values, "MainPID");
		long uptime = 0;
		double startedAt = 0;
		var identity = string.Empty;

		if (pid != 0)
		{
			try
			{
				var machineUptime = ProcFs.MachineUptimeSeconds();
				var ticks = ProcFs.ClockTicksPerSecond();
				var startTicks = ProcFs.StartTicks(pid);
				var startedAfterBoot = startTicks / (double) ticks;

				uptime = (long) Math.Max(0, machineUptime - startedAfterBoot);
				startedAt = time.GetUtcNow().ToUnixTimeMilliseconds() / 1000d - (machineUptime - startedAfterBoot);
				identity = $"{ProcFs.BootId()}:{pid}:{startTicks}";
			}
			catch (Exception exception) when (exception is IOException or FormatException or UnauthorizedAccessException)
			{
				// Процесс мог завершиться между systemctl show и чтением /proc. Снимок остаётся без
				// идентичности, это обрабатывает политика готовности.
			}
		}

		return new ServiceStatusPayload
		{
			Active = values.GetValueOrDefault("ActiveState") == "active",
			State = values.GetValueOrDefault("ActiveState") ?? "unknown",
			SubState = values.GetValueOrDefault("SubState") ?? "unknown",
			Pid = pid,
			Memory = values.GetValueOrDefault("MemoryCurrent") == "[not set]" ? 0 : Long(values, "MemoryCurrent"),
			CpuNs = Long(values, "CPUUsageNSec"),
			Restarts = Int(values, "NRestarts"),
			Uptime = uptime,
			Started = values.GetValueOrDefault("ExecMainStartTimestamp") ?? string.Empty,
			StartedAt = startedAt,
			Instance = identity,
			Unit = server.Unit,
		};
	}

	public async Task<bool> IsActiveAsync(RegisteredServer server, CancellationToken ct)
	{
		var result = await RunAsync(systemctl, ["is-active", "--quiet", server.Unit], ct).ConfigureAwait(false);
		return result.ExitCode == 0;
	}

	public async Task ServiceAsync(RegisteredServer server, string action, TimeSpan timeout, CancellationToken ct)
	{
		// Протокол уже ограничивает набор действий; привилегированная сторона проверяет его
		// ещё раз.
		if (action is not ("start" or "stop" or "restart"))
		{
			throw new ArgumentException($"Недопустимое действие службы: {action}", nameof(action));
		}

		using var limited = CancellationTokenSource.CreateLinkedTokenSource(ct);
		limited.CancelAfter(timeout);

		var result = await RunAsync(systemctl, [action, server.Unit], limited.Token).ConfigureAwait(false);
		if (result.ExitCode != 0)
		{
			throw new IOException($"systemctl {action} вернул {result.ExitCode}: {result.StandardError.Trim()}");
		}
	}

	public async Task<IReadOnlyList<LogEntryPayload>> LogsAsync(RegisteredServer server, int count, CancellationToken ct)
	{
		var limited = Math.Clamp(count, 20, 2000);
		var result = await RunAsync(
				journalctl,
				["--unit", server.Unit, "--no-pager", "--output=json", "--lines", limited.ToString(CultureInfo.InvariantCulture)],
				ct)
			.ConfigureAwait(false);

		return [.. Parse(result.StandardOutput).Entries];
	}

	/// <summary>
	/// Всё, что появилось после курсора. Курсор — строка journald, панель только передаёт
	/// её обратно.
	/// </summary>
	public async Task<LogPagePayload> LogsFromAsync(RegisteredServer server, string cursor, CancellationToken ct)
	{
		string[] arguments = cursor is "tail" or ""
			? ["--unit", server.Unit, "--no-pager", "--output=json", "--lines", "0", "--show-cursor"]
			: ["--unit", server.Unit, "--no-pager", "--output=json", "--after-cursor", cursor, "--lines", "500", "--show-cursor"];

		var result = await RunAsync(journalctl, arguments, ct).ConfigureAwait(false);
		var page = Parse(result.StandardOutput);
		return page with { Cursor = page.Cursor == "tail" ? cursor : page.Cursor };
	}

	/// <summary>
	/// Отправить строку в консоль и при необходимости прочитать ответ.
	/// </summary>
	/// <remarks>
	/// Курсор снимается до записи, иначе в ответ попадут строки, появившиеся раньше команды.
	/// Вызывающий не видит этих двух шагов и не может их переставить.
	/// </remarks>
	public async Task<ConsoleEchoPayload> ConsoleAsync(
		RegisteredServer server, string line, bool capture, CancellationToken ct)
	{
		var cursor = capture ? await CursorAsync(server, ct).ConfigureAwait(false) : null;

		await WriteConsoleAsync(server, line, ct).ConfigureAwait(false);

		if (cursor is null)
		{
			return new ConsoleEchoPayload();
		}

		await Task.Delay(ConsoleReadBackDelay, ct).ConfigureAwait(false);
		var page = await LogsFromAsync(server, cursor, ct).ConfigureAwait(false);

		return new ConsoleEchoPayload
		{
			Lines = [.. page.Entries.Select(entry => entry.Message)],
			WaitedMilliseconds = (long) ConsoleReadBackDelay.TotalMilliseconds,
		};
	}

	public async Task<ConsoleCapabilityPayload> CapabilitiesAsync(RegisteredServer server, CancellationToken ct)
	{
		switch (server.CommandTransport)
		{
			case "fifo":
				{
					var exists = await IsFifoAsync(server.ConsoleFifo, ct).ConfigureAwait(false);
					var running = exists && await IsActiveAsync(server, ct).ConfigureAwait(false);

					return new ConsoleCapabilityPayload
					{
						Transport = "fifo",
						Available = running,
						Reason = running
							? null
							: exists
								? Phrase.From("Сервер остановлен: консоль недоступна")
								: Phrase.From("Канал консоли не создан; перезапустите сервер"),
					};
				}

			case "screen":
				{
					if (!File.Exists(screen))
					{
						return new ConsoleCapabilityPayload { Transport = "screen", Reason = Phrase.From("GNU Screen не установлен") };
					}

					var probe = await RunAsync(screen, ["-S", server.ScreenSession, "-Q", "select", "."], ct)
						.ConfigureAwait(false);
					return new ConsoleCapabilityPayload
					{
						Transport = "screen",
						Available = probe.ExitCode == 0,
						Reason = probe.ExitCode == 0 ? null : Phrase.Format($"Сессия screen {server.ScreenSession} не найдена"),
					};
				}

			case "server.sh":
				{
					var executable = File.Exists(server.CommandRunner)
						&& File.GetUnixFileMode(server.CommandRunner).HasFlag(UnixFileMode.UserExecute);

					return new ConsoleCapabilityPayload
					{
						Transport = "server.sh",
						Available = executable,
						Reason = executable ? null : Phrase.Format($"{server.CommandRunner} не является исполняемым"),
					};
				}

			default:
				return new ConsoleCapabilityPayload
				{
					Transport = "disabled",
					Reason = Phrase.From("Передача команд отключена в реестре инстансов"),
				};
		}
	}

	private async Task<string> CursorAsync(RegisteredServer server, CancellationToken ct)
	{
		var page = await LogsFromAsync(server, "tail", ct).ConfigureAwait(false);
		return page.Cursor;
	}

	private async Task WriteConsoleAsync(RegisteredServer server, string line, CancellationToken ct)
	{
		switch (server.CommandTransport)
		{
			case "fifo":
				if (!await IsFifoAsync(server.ConsoleFifo, ct).ConfigureAwait(false))
				{
					throw new IOException("Настроенный путь консоли не является каналом FIFO");
				}

				// Запись в существующий канал. Открытие на запись без читателя заблокировалось бы,
				// поэтому канал создаёт vs-console-wrapper, и живёт он столько же, сколько сервер.
				await using (var fifo = new FileStream(
					server.ConsoleFifo, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
				{
					var bytes = Encoding.UTF8.GetBytes(line + "\n");
					await fifo.WriteAsync(bytes, ct).ConfigureAwait(false);
					await fifo.FlushAsync(ct).ConfigureAwait(false);
				}

				return;

			case "screen":
				await RunAsync(screen, ["-S", server.ScreenSession, "-p", "0", "-X", "stuff", line + "\r"], ct)
					.ConfigureAwait(false);
				return;

			case "server.sh":
				await RunAsync(server.CommandRunner, ["command", line], ct).ConfigureAwait(false);
				return;

			default:
				throw new IOException("Передача команд отключена; настройте command_transport для этого сервера");
		}
	}

	/// <summary>
	/// Действительно ли это именованный канал.
	/// </summary>
	/// <remarks>
	/// Проверяется через /usr/bin/test, а не stat(2) по P/Invoke: раскладка struct stat зависит
	/// от libc и архитектуры. Ошибка в ней привела бы к записи команды в обычный файл, который
	/// никто не читает.
	///
	/// Путь берётся из реестра, и без проверки запись в подменённый обычный файл прошла бы
	/// без ошибки.
	/// </remarks>
	private static async Task<bool> IsFifoAsync(string path, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}

		try
		{
			var result = await RunAsync("/usr/bin/test", ["-p", path], ct).ConfigureAwait(false);
			return result.ExitCode == 0;
		}
		catch (IOException)
		{
			return false;
		}
	}

	private static LogPagePayload Parse(string json)
	{
		var entries = new List<LogEntryPayload>();
		var cursor = "tail";

		foreach (var line in json.Split('\n'))
		{
			var text = line.Trim();
			if (text.Length == 0)
			{
				continue;
			}

			if (text.StartsWith("-- cursor: ", StringComparison.Ordinal))
			{
				cursor = text["-- cursor: ".Length..].Trim();
				continue;
			}

			try
			{
				var item = JsonDocument.Parse(text).RootElement;
				entries.Add(new LogEntryPayload
				{
					Timestamp = Microseconds(item, "__REALTIME_TIMESTAMP"),
					Message = Text(item, "MESSAGE"),
					Priority = int.TryParse(Text(item, "PRIORITY"), CultureInfo.InvariantCulture, out var priority)
						? priority
						: 6,
					Source = Text(item, "SYSLOG_IDENTIFIER"),
					Line = ShortIso(item),
				});

				// Курсор последней записи используется, если journald не напечатал отдельную строку
				// «-- cursor: ».
				if (Text(item, "__CURSOR") is { Length: > 0 } entryCursor)
				{
					cursor = entryCursor;
				}
			}
			catch (JsonException)
			{
				// journald иногда печатает служебные строки вне JSON; такая строка пропускается.
			}
		}

		return new LogPagePayload { Entries = entries, Cursor = cursor };
	}

	/// <summary>
	/// Запись в формате <c>journalctl --output=short-iso</c>: <c>2026-09-21T14:30:00+0300
	/// host vintagestory[1234]: сообщение</c>. По этой строке работают классификация и разбор
	/// времени.
	/// </summary>
	/// <remarks>
	/// Правила journalctl: идентификатор — <c>SYSLOG_IDENTIFIER</c>, иначе <c>_COMM</c>;
	/// pid — <c>SYSLOG_PID</c>, иначе <c>_PID</c>; без pid квадратных скобок нет. Смещение как
	/// <c>%z</c> у strftime, без двоеточия. Время местное для демона.
	/// </remarks>
	private static string ShortIso(JsonElement item)
	{
		var microseconds = long.TryParse(Text(item, "__REALTIME_TIMESTAMP"), CultureInfo.InvariantCulture, out var value)
			? value
			: 0;
		var local = DateTimeOffset.FromUnixTimeMilliseconds(microseconds / 1000).ToLocalTime();
		var offset = local.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", string.Empty, StringComparison.Ordinal);
		var stamp = local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + offset;

		var identifier = Text(item, "SYSLOG_IDENTIFIER") is { Length: > 0 } syslog ? syslog : Text(item, "_COMM");
		var pid = Text(item, "SYSLOG_PID") is { Length: > 0 } syslogPid ? syslogPid : Text(item, "_PID");
		var process = pid.Length > 0 ? $"{identifier}[{pid}]" : identifier;

		return $"{stamp} {Text(item, "_HOSTNAME")} {process}: {Text(item, "MESSAGE")}";
	}

	private static string Text(JsonElement item, string name) =>
		item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
			? value.GetString() ?? string.Empty
			: string.Empty;

	private static double Microseconds(JsonElement item, string name) =>
		double.TryParse(Text(item, name), CultureInfo.InvariantCulture, out var value) ? value / 1_000_000d : 0;

	private static Dictionary<string, string> Properties(string output)
	{
		var values = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var line in output.Split('\n'))
		{
			var separator = line.IndexOf('=', StringComparison.Ordinal);
			if (separator > 0)
			{
				values[line[..separator]] = line[(separator + 1)..].TrimEnd('\r');
			}
		}

		return values;
	}

	private static int Int(Dictionary<string, string> values, string key) =>
		int.TryParse(values.GetValueOrDefault(key), CultureInfo.InvariantCulture, out var value) ? value : 0;

	private static long Long(Dictionary<string, string> values, string key) =>
		long.TryParse(values.GetValueOrDefault(key), CultureInfo.InvariantCulture, out var value) ? value : 0;

	private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

	/// <summary>
	/// Запуск внешней команды. Аргументы передаются списком, без оболочки, поэтому экранирование
	/// не нужно.
	/// </summary>
	private static async Task<CommandResult> RunAsync(string file, string[] arguments, CancellationToken ct)
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

		using var process = Process.Start(start) ?? throw new IOException($"Не удалось запустить {file}");

		var stdout = process.StandardOutput.ReadToEndAsync(ct);
		var stderr = process.StandardError.ReadToEndAsync(ct);
		await process.WaitForExitAsync(ct).ConfigureAwait(false);

		return new CommandResult(
			process.ExitCode,
			await stdout.ConfigureAwait(false),
			await stderr.ConfigureAwait(false));
	}
}
