using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using Panel.Agent.Runtime;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Supervision;

/// <summary>
/// Игровой сервер как дочерний процесс демона на Windows, вместо юнита systemd.
/// </summary>
/// <remarks>
/// Демон сам запускает сервер и держит его stdin, через который команды попадают в игру.
/// Вывод сервер пишет в <c>&lt;run_dir&gt;/&lt;id&gt;.console.log</c> собственным дескриптором
/// (<see cref="ConsoleLog"/>).
///
/// Сервер переживает перезапуск демона (Windows не завершает осиротевшие процессы), и демон
/// находит его по pid и моменту создания из файла состояния. Канал stdin восстановить нельзя:
/// после перезапуска демона консоль сервера недоступна до перезапуска самого сервера, и
/// <see cref="CapabilitiesAsync"/> сообщает об этом заранее.
///
/// Каналы stdin и замки служебных действий — единственное изменяемое состояние демона. Оно
/// относится к серверам, а не к запросам, поэтому хранится здесь.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SupervisedBackend : IServiceBackend
{
	/// <summary>Имя транспорта консоли в реестре; на Windows допустимо только оно.</summary>
	public const string Transport = "supervised";

	/// <summary>Сколько ждать, пока игра сохранит мир и выйдет по <c>/stop</c>.</summary>
	private static readonly TimeSpan stopGrace = TimeSpan.FromSeconds(90);

	private static readonly TimeSpan stopPoll = TimeSpan.FromSeconds(1);

	private static readonly TimeSpan killWait = TimeSpan.FromSeconds(10);

	private readonly string runDirectory;
	private readonly TimeProvider time;
	private readonly ConcurrentDictionary<string, AnonymousPipeServerStream> consoles = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);

	public SupervisedBackend(string runDirectory, TimeProvider? time = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(runDirectory);
		this.runDirectory = runDirectory;
		this.time = time ?? TimeProvider.System;
	}

	public string Name => "supervised";

	/// <summary>
	/// Пауза перед чтением ответа консоли. Задаётся отдельно от systemd-бэкенда: здесь чтение
	/// идёт по смещению в файле.
	/// </summary>
	public TimeSpan ConsoleReadBackDelay { get; init; } = TimeSpan.FromMilliseconds(1200);

	public Task<ServiceStatusPayload> StatusAsync(RegisteredServer server, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(server);

		var state = State(server);
		var sample = ProcessFacts.Sample(state.Pid);
		var running = sample is not null && state.IsSameProcess(sample.Created);
		var now = time.GetUtcNow().ToUnixTimeMilliseconds() / 1000d;

		return Task.FromResult(new ServiceStatusPayload
		{
			Active = running,
			State = running ? "active" : "inactive",
			SubState = running ? "running" : "dead",
			Pid = running ? state.Pid : 0,
			Memory = running ? sample!.WorkingSet : 0,
			CpuNs = running ? sample!.CpuNanoseconds : 0,
			Restarts = state.Restarts,
			Uptime = running ? (long) Math.Max(0, now - state.StartedAt) : 0,
			Started = running
				? DateTimeOffset.FromUnixTimeMilliseconds((long) (state.StartedAt * 1000)).ToLocalTime()
					.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
				: string.Empty,
			StartedAt = running ? state.StartedAt : 0,

			// Идентичность в той же тройке, что у systemd (ADR-04), с меткой платформы на месте
			// boot id. Момент создания в тиках FILETIME абсолютен и после перезагрузки не
			// повторится, поэтому boot id не нужен.
			Instance = running
				? string.Create(CultureInfo.InvariantCulture, $"windows:{state.Pid}:{sample!.CreatedFileTime}")
				: string.Empty,
			Unit = server.Unit,
		});
	}

	public Task<bool> IsActiveAsync(RegisteredServer server, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(server);
		return Task.FromResult(IsRunning(State(server)));
	}

	public async Task ServiceAsync(RegisteredServer server, string action, TimeSpan timeout, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(server);

		// Протокол уже ограничивает набор действий; привилегированная сторона проверяет его
		// ещё раз.
		if (action is not ("start" or "stop" or "restart"))
		{
			throw new ArgumentException($"Недопустимое действие службы: {action}", nameof(action));
		}

		var gate = gates.GetOrAdd(server.Id.Value, _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			if (action is "stop" or "restart")
			{
				await StopAsync(server, timeout, ct).ConfigureAwait(false);
			}

			if (action is "start" or "restart")
			{
				await StartAsync(server, ct).ConfigureAwait(false);
			}
		}
		finally
		{
			gate.Release();
		}
	}

	public Task<IReadOnlyList<LogEntryPayload>> LogsAsync(RegisteredServer server, int count, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(server);

		var lines = ConsoleLog.Tail(ConsolePath(server), Math.Clamp(count, 20, 2000));
		return Task.FromResult<IReadOnlyList<LogEntryPayload>>(Entries(lines));
	}

	public Task<LogPagePayload> LogsFromAsync(RegisteredServer server, string cursor, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(server);

		var chunk = ConsoleLog.After(ConsolePath(server), cursor ?? "tail");
		return Task.FromResult(new LogPagePayload { Entries = Entries(chunk.Lines), Cursor = chunk.Cursor });
	}

	/// <summary>
	/// Как у systemd, конец журнала запоминается до записи, иначе в ответ попадут строки,
	/// появившиеся раньше команды.
	/// </summary>
	public async Task<ConsoleEchoPayload> ConsoleAsync(
		RegisteredServer server, string line, bool capture, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(server);

		var log = ConsolePath(server);
		var cursor = capture ? ConsoleLog.End(log) : null;

		await WriteConsoleAsync(server, line, ct).ConfigureAwait(false);

		if (cursor is null)
		{
			return new ConsoleEchoPayload();
		}

		await Task.Delay(ConsoleReadBackDelay, time, ct).ConfigureAwait(false);
		return new ConsoleEchoPayload
		{
			Lines = ConsoleLog.After(log, cursor).Lines,
			WaitedMilliseconds = (long) ConsoleReadBackDelay.TotalMilliseconds,
		};
	}

	public Task<ConsoleCapabilityPayload> CapabilitiesAsync(RegisteredServer server, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(server);

		if (server.CommandTransport != Transport)
		{
			return Task.FromResult(new ConsoleCapabilityPayload
			{
				Transport = "disabled",
				Reason = Phrase.From("Передача команд отключена в реестре инстансов"),
			});
		}

		if (!IsRunning(State(server)))
		{
			return Task.FromResult(new ConsoleCapabilityPayload { Transport = Transport, Reason = Phrase.From("Сервер не запущен") });
		}

		if (!consoles.TryGetValue(server.Id.Value, out var input) || !input.IsConnected)
		{
			return Task.FromResult(new ConsoleCapabilityPayload
			{
				Transport = Transport,
				Reason = Phrase.From("Служба панели была перезапущена и потеряла консоль этого сервера. "
					+ "Команды снова заработают после его перезапуска."),
			});
		}

		return Task.FromResult(new ConsoleCapabilityPayload { Transport = Transport, Available = true });
	}

	/// <summary>
	/// Служба ОС не ставится, её роль выполняет демон. Здесь проверяется, что сервер готов к
	/// запуску, и заводится файл состояния с пустым pid и путём журнала консоли.
	/// </summary>
	/// <returns>Путь к файлу состояния.</returns>
	public async Task<Result<string>> AdoptAsync(RegisteredServer server, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(server);

		if (server.CommandTransport != Transport)
		{
			return Failure.Conflict("На Windows доступен только транспорт консоли supervised");
		}

		if (DotnetPath() is null)
		{
			return Failure.Unavailable("dotnet не найден: установите среду .NET, которая нужна игре");
		}

		if (server.ProgramDirectory.Length == 0 || !File.Exists(Path.Combine(server.ProgramDirectory, "VintagestoryServer.dll")))
		{
			return Failure.Unavailable("В каталоге программы сервера нет VintagestoryServer.dll");
		}

		var path = SupervisorState.PathFor(runDirectory, server.Id.Value);
		var state = SupervisorState.Read(path) with { Pid = 0, Log = ConsolePath(server) };
		var written = await state.WriteAsync(path, ct).ConfigureAwait(false);
		return written.IsSuccess ? path : Result.Fail<string>(written.Failure!);
	}

	/// <summary>
	/// Остановить сервер и удалить его состояние. Файлы сервера не трогаются.
	/// </summary>
	/// <remarks>
	/// Сначала <c>/stop</c> с обычным ожиданием, а не принудительное завершение, чтобы не
	/// потерять последние минуты мира, если оператор передумает во время удаления дерева.
	/// </remarks>
	public async Task<Result<Done>> ForgetAsync(RegisteredServer server, TimeSpan timeout, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(server);

		await ServiceAsync(server, "stop", timeout, ct).ConfigureAwait(false);
		if (IsRunning(State(server)))
		{
			return Failure.Conflict($"Не удалось остановить {server.Unit}; остановите его вручную перед удалением сервера");
		}

		File.Delete(SupervisorState.PathFor(runDirectory, server.Id.Value));
		File.Delete(ConsolePath(server));
		File.Delete(ConsoleLog.PreviousPathFor(runDirectory, server.Id.Value));
		gates.TryRemove(server.Id.Value, out _);
		return Done.Ok;
	}

	private SupervisorState State(RegisteredServer server) =>
		SupervisorState.Read(SupervisorState.PathFor(runDirectory, server.Id.Value));

	private string ConsolePath(RegisteredServer server) => ConsoleLog.PathFor(runDirectory, server.Id.Value);

	private static bool IsRunning(SupervisorState state) =>
		ProcessFacts.Sample(state.Pid) is { } sample && state.IsSameProcess(sample.Created);

	private async Task StartAsync(RegisteredServer server, CancellationToken ct)
	{
		var path = SupervisorState.PathFor(runDirectory, server.Id.Value);
		var previous = SupervisorState.Read(path);
		if (IsRunning(previous))
		{
			return;
		}

		var dotnet = DotnetPath()
			?? throw new IOException("dotnet не найден: установите среду .NET, которая нужна игре");

		var program = Path.Combine(server.ProgramDirectory, "VintagestoryServer.dll");
		if (server.ProgramDirectory.Length == 0 || !File.Exists(program))
		{
			throw new IOException($"Программа сервера {program} не найдена");
		}

		Directory.CreateDirectory(runDirectory);
		var log = ConsolePath(server);
		ConsoleLog.Rotate(log, ConsoleLog.PreviousPathFor(runDirectory, server.Id.Value));

		var started = ChildProcess.Start(dotnet, [program, "--dataPath", server.DataDirectory], server.ProgramDirectory, log);
		if (consoles.TryRemove(server.Id.Value, out var stale))
		{
			await stale.DisposeAsync().ConfigureAwait(false);
		}

		consoles[server.Id.Value] = started.Input;

		var state = previous with
		{
			Pid = started.Pid,
			Created = ProcessFacts.Sample(started.Pid)?.Created ?? 0,
			StartedAt = time.GetUtcNow().ToUnixTimeMilliseconds() / 1000d,
			Log = log,
			Restarts = previous.Restarts + 1,
		};

		var written = await state.WriteAsync(path, ct).ConfigureAwait(false);
		if (!written.IsSuccess)
		{
			// Сервер без записи о нём демон не увидит ни в статусе, ни при остановке, поэтому
			// его лучше завершить.
			ProcessFacts.Kill(state, killWait);
			await DropConsoleAsync(server).ConfigureAwait(false);
			throw new IOException(written.Failure!.Message);
		}
	}

	private async Task StopAsync(RegisteredServer server, TimeSpan timeout, CancellationToken ct)
	{
		var path = SupervisorState.PathFor(runDirectory, server.Id.Value);
		var state = SupervisorState.Read(path);

		if (IsRunning(state))
		{
			// Попросить игру выйти штатно, с сохранением мира.
			try
			{
				await WriteConsoleAsync(server, "/stop", ct).ConfigureAwait(false);
			}
			catch (IOException)
			{
				// Консоли нет: остаётся подождать и завершить принудительно.
			}

			var deadline = time.GetUtcNow() + (timeout < stopGrace ? timeout : stopGrace);
			while (IsRunning(state) && time.GetUtcNow() < deadline)
			{
				await Task.Delay(stopPoll, time, ct).ConfigureAwait(false);
			}

			if (IsRunning(state))
			{
				ProcessFacts.Kill(state, killWait);
			}
		}

		await DropConsoleAsync(server).ConfigureAwait(false);

		var written = await (state with { Pid = 0 }).WriteAsync(path, ct).ConfigureAwait(false);
		if (!written.IsSuccess)
		{
			throw new IOException(written.Failure!.Message);
		}
	}

	private async Task WriteConsoleAsync(RegisteredServer server, string line, CancellationToken ct)
	{
		if (server.CommandTransport != Transport)
		{
			throw new IOException("Передача команд отключена; настройте command_transport для этого сервера");
		}

		if (!consoles.TryGetValue(server.Id.Value, out var input) || !input.IsConnected)
		{
			throw new IOException("Консоль этого сервера недоступна: перезапустите сервер");
		}

		try
		{
			await input.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n"), ct).ConfigureAwait(false);
			await input.FlushAsync(ct).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or ObjectDisposedException)
		{
			throw new IOException($"Не удалось записать в консоль сервера: {exception.Message}", exception);
		}
	}

	private async Task DropConsoleAsync(RegisteredServer server)
	{
		if (consoles.TryRemove(server.Id.Value, out var input))
		{
			await input.DisposeAsync().ConfigureAwait(false);
		}
	}

	/// <summary>Строка консоли как запись журнала; время — момент чтения.</summary>
	private List<LogEntryPayload> Entries(IReadOnlyList<string> lines)
	{
		var now = time.GetUtcNow().ToUnixTimeMilliseconds() / 1000d;
		return [.. lines.Select(line => new LogEntryPayload
		{
			Timestamp = now,
			Message = line,
			Source = "vintagestory",
			Line = line,
		})];
	}

	/// <summary>Путь к dotnet на Windows: известные места, затем PATH.</summary>
	public static string? DotnetPath()
	{
		string[] known =
		[
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "dotnet.exe"),
		];

		var searched = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Where(Path.IsPathFullyQualified)
			.Select(directory => Path.Combine(directory, "dotnet.exe"));

		// Каталог программ может быть не определён (x86 на 32-битной системе), и Path.Combine
		// дал бы относительный путь от текущего каталога.
		return known.Where(Path.IsPathFullyQualified).Concat(searched).FirstOrDefault(File.Exists);
	}
}
