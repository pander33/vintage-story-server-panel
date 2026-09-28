using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Agent.Content;
using Panel.Agent.Runtime;
using Panel.Agent.Telemetry;
using Panel.Contracts;
using Panel.Contracts.Agent;
using Panel.Contracts.ServerCommands;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.SharedKernel;

namespace Panel.Agent.Transport;

/// <summary>
/// Таблица операций привилегированной стороны: имя операции сопоставлено с действием, число
/// и смысл аргументов закреплены.
/// </summary>
/// <remarks>
/// Пути демон выводит сам из реестра, панель присылает только идентификатор сервера.
///
/// Каждый аргумент проверяется здесь, даже если его уже проверил веб-процесс.
///
/// Платформенных типов диспетчер не знает: бэкенд службы приходит интерфейсом, остальные
/// группы операций — списками. Если какой-то группы в сборке нет, её операции получают отказ
/// «не поддерживается», а не «неизвестная операция».
/// </remarks>
public sealed class AgentDispatcher
{
	/// <summary>Предел ожидания systemctl.</summary>
	private static readonly TimeSpan serviceTimeout = TimeSpan.FromSeconds(180);

	/// <summary>Все имена операций протокола, чтобы отличить неподдерживаемую от неизвестной.</summary>
	private static readonly FrozenSet<string> knownOperations = typeof(AgentProtocol)
		.GetFields(BindingFlags.Public | BindingFlags.Static)
		.Where(field => field.IsLiteral && field.Name.StartsWith("Op", StringComparison.Ordinal))
		.Select(field => (string) field.GetRawConstantValue()!)
		.ToFrozenSet(StringComparer.Ordinal);

	/// <summary>Операции, которые диспетчер исполняет сам на любой платформе.</summary>
	private static readonly FrozenSet<string> coreOperations = new[]
	{
		AgentProtocol.OpPing, AgentProtocol.OpStatus, AgentProtocol.OpTelemetry, AgentProtocol.OpCapabilities,
		AgentProtocol.OpService, AgentProtocol.OpLogs, AgentProtocol.OpLogsFrom, AgentProtocol.OpConsole,
		AgentProtocol.OpConsoleRaw, AgentProtocol.OpWorldConfigAction, AgentProtocol.OpPlayerAction, AgentProtocol.OpGameEvents,
	}.ToFrozenSet(StringComparer.Ordinal);

	private readonly IRegistryStore registry;
	private readonly IServiceBackend backend;
	private readonly TelemetryFileReader telemetry;
	private readonly GameEventLog events;
	private readonly IReadOnlyList<IServerOperations> serverOperations;
	private readonly IReadOnlyList<IRegistryOperations> registryOperations;
	private readonly OperationTracker tracker;

	public AgentDispatcher(
		IRegistryStore registry,
		IServiceBackend backend,
		TelemetryFileReader telemetry,
		GameEventLog events,
		IReadOnlyList<IServerOperations> serverOperations,
		IReadOnlyList<IRegistryOperations> registryOperations,
		OperationTracker? tracker = null)
	{
		this.tracker = tracker ?? new OperationTracker(TimeProvider.System);
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(backend);
		ArgumentNullException.ThrowIfNull(telemetry);
		ArgumentNullException.ThrowIfNull(events);
		ArgumentNullException.ThrowIfNull(serverOperations);
		ArgumentNullException.ThrowIfNull(registryOperations);

		this.registry = registry;
		this.backend = backend;
		this.telemetry = telemetry;
		this.events = events;
		this.serverOperations = serverOperations;
		this.registryOperations = registryOperations;
	}

	public async Task<AgentReply> DispatchAsync(AgentRequest request, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (request.Op == AgentProtocol.OpPing)
		{
			return AgentReply.Success(new JsonObject { ["ok"] = true });
		}

		var instance = InstanceId.Create(request.Instance);
		if (!instance.IsSuccess)
		{
			return AgentReply.Failure("instance", instance.Failure!.Text);
		}

		// Серверу «-» адресуются только операции реестра, и операции реестра — только ему.
		var registryGroup = registryOperations.FirstOrDefault(group => group.Serves(request.Op));
		if (instance.Value.IsRegistry)
		{
			if (registryGroup is not null)
			{
				using var deleting = request.Op == AgentProtocol.OpInstanceDelete && request.Args.Count > 0
					? tracker.Begin(request.Args[0], request.Op, [Phrase.From("Удаление сервера")], Phrase.From("Удаление сервера"))
					: null;
				return await registryGroup.ExecuteAsync(request, ct).ConfigureAwait(false);
			}

			return Unsupported(request.Op)
				?? AgentReply.Failure("instance", $"Операция {request.Op} не адресуется реестру");
		}

		if (registryGroup is not null)
		{
			return AgentReply.Failure("instance", "Операции реестра адресуются серверу «-»");
		}

		var server = await ResolveAsync(instance.Value, ct).ConfigureAwait(false);
		if (!server.IsSuccess)
		{
			return AgentReply.Failure(Code(server.Failure!), server.Failure!.Text);
		}

		try
		{
			using var scope = Describe(request) is { } title
				? tracker.Begin(server.Value.Id.Value, request.Op, [title], title)
				: null;
			return await ExecuteAsync(request, server.Value, ct).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
			return AgentReply.Failure("timeout", "Операция не завершилась за отведённое время");
		}
		catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
		{
			return AgentReply.Failure("io", exception.Message);
		}
	}

	/// <summary>
	/// Название долгой операции для показа хода, либо <see langword="null"/>, если операция мгновенна
	/// или сама ведёт ход по стадиям (копия, восстановление, выгрузка пакета).
	/// </summary>
	private static Phrase? Describe(AgentRequest request) => request.Op switch
	{
		AgentProtocol.OpService => Argument(request, 0) switch
		{
			"stop" => Phrase.From("Остановка сервера"),
			"start" => Phrase.From("Запуск сервера"),
			"restart" => Phrase.From("Перезапуск сервера"),
			_ => null,
		},
		AgentProtocol.OpModInstall => Phrase.From("Установка мода"),
		AgentProtocol.OpModRollback => Phrase.From("Откат мода"),
		AgentProtocol.OpModProfileApply => Phrase.From("Применение профиля модов"),
		AgentProtocol.OpWorldImport => Phrase.From("Загрузка мира"),
		AgentProtocol.OpWorldExport => Phrase.From("Выгрузка мира"),
		AgentProtocol.OpWorldClone => Phrase.From("Копирование мира"),
		AgentProtocol.OpWorldActivate => Phrase.From("Смена активного мира"),
		AgentProtocol.OpWorldCreate => Phrase.From("Создание мира"),
		AgentProtocol.OpWorldPutAway => Phrase.From("Отправка мира в архив"),
		AgentProtocol.OpWorldRestore => Phrase.From("Возврат мира из архива"),
		AgentProtocol.OpGameUpdateApply => Phrase.From("Замена программы сервера"),
		AgentProtocol.OpGameUpdateRollback => Phrase.From("Откат программы сервера"),
		AgentProtocol.OpBackupRetention => Argument(request, 3) == "apply" ? Phrase.From("Очистка старых копий") : null,
		_ => null,
	};

	private async Task<AgentReply> ExecuteAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
	{
		switch (request.Op)
		{
			case AgentProtocol.OpStatus:
				return Ok(await backend.StatusAsync(server, ct).ConfigureAwait(false));

			case AgentProtocol.OpTelemetry:
				return Ok(await telemetry.ReadAsync(server.TelemetryFile, ct).ConfigureAwait(false));

			case AgentProtocol.OpCapabilities:
				return Ok(await backend.CapabilitiesAsync(server, ct).ConfigureAwait(false));

			case AgentProtocol.OpService:
				{
					if (Argument(request, 0) is not { } action)
					{
						return AgentReply.Failure("args", "Не указано действие службы");
					}

					await backend.ServiceAsync(server, action, serviceTimeout, ct).ConfigureAwait(false);
					return AgentReply.Success(new JsonObject { ["ok"] = true, ["action"] = action });
				}

			case AgentProtocol.OpLogs:
				{
					var count = int.TryParse(Argument(request, 0), CultureInfo.InvariantCulture, out var value)
						? value
						: 300;
					return Ok(new LogPagePayload
					{
						Entries = await backend.LogsAsync(server, count, ct).ConfigureAwait(false),
					});
				}

			case AgentProtocol.OpLogsFrom:
				return Ok(await backend
					.LogsFromAsync(server, Argument(request, 0) ?? "tail", ct)
					.ConfigureAwait(false));

			case AgentProtocol.OpConsole:
				return await ConsoleAsync(request, server, ct).ConfigureAwait(false);

			case AgentProtocol.OpConsoleRaw:
				return await RawConsoleAsync(request, server, ct).ConfigureAwait(false);

			case AgentProtocol.OpWorldConfigAction:
				return await WorldConfigAsync(request, server, ct).ConfigureAwait(false);

			case AgentProtocol.OpPlayerAction:
				return await PlayerActionAsync(request, server, ct).ConfigureAwait(false);

			case AgentProtocol.OpGameEvents:
				{
					if (!int.TryParse(Argument(request, 0), CultureInfo.InvariantCulture, out var count) || count < 0)
					{
						return AgentReply.Failure("args", "Недопустимое число строк");
					}

					var telemetry = await this.telemetry.ReadAsync(server.TelemetryFile, ct).ConfigureAwait(false);
					return Ok(await events.ReadAsync(server.GameEventsFile, count, telemetry, ct).ConfigureAwait(false));
				}

			default:
				{
					// Операции над содержимым и настройками вынесены в группы: их 32, и в общей таблице
					// потерялись бы служба, журнал и консоль.
					var group = serverOperations.FirstOrDefault(candidate => candidate.Serves(request.Op));
					if (group is not null)
					{
						return await group.ExecuteAsync(request, server, ct).ConfigureAwait(false);
					}

					return Unsupported(request.Op) ?? AgentReply.Failure("op", $"Неизвестная операция: {request.Op}");
				}
		}
	}

	/// <summary>
	/// Операция протокола, группы которой нет в этой сборке демона. Отказ отличается от
	/// «неизвестной операции»: запрос правильный, но демон его не поддерживает.
	/// </summary>
	private AgentReply? Unsupported(string op) =>
		knownOperations.Contains(op)
		&& !coreOperations.Contains(op)
		&& !serverOperations.Any(group => group.Serves(op))
		&& !registryOperations.Any(group => group.Serves(op))
			? AgentReply.Failure("unsupported", $"Операция {op} на платформе демона ({backend.Name}) пока не поддерживается")
			: null;

	/// <summary>
	/// Живая конфигурация мира. Как и для консоли, демон получает разобранные части (действие,
	/// тип, имя, значение) и собирает строку сам по своей копии правила. Шаблон нового мира
	/// сюда не приходит: это другой тип и другая операция.
	/// </summary>
	private async Task<AgentReply> WorldConfigAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
	{
		var command = WorldConfigCommand.Create(
			Argument(request, 0), Argument(request, 1), Argument(request, 2), Argument(request, 3));
		if (!command.IsSuccess)
		{
			return AgentReply.Failure("invalid", command.Failure!.Text);
		}

		if (!await backend.IsActiveAsync(server, ct).ConfigureAwait(false))
		{
			return AgentReply.Failure("conflict", "Сервер остановлен: команду некому выполнить");
		}

		await backend.ConsoleAsync(server, command.Value.Render(), capture: false, ct).ConfigureAwait(false);

		return AgentReply.Success(new JsonObject
		{
			["ok"] = true,
			["action"] = command.Value.Action,
			["datatype"] = command.Value.DataType,
			["name"] = command.Value.Name,
		});
	}

	/// <summary>
	/// Консоль. Демон получает действие и значение, а не готовую строку команды, иначе
	/// веб-процесс мог бы прислать что угодно в обход списка разрешённых. Строка собирается
	/// здесь по своей копии каталога.
	/// </summary>
	private async Task<AgentReply> ConsoleAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
	{
		var spec = ServerCommandCatalogue.Find(Argument(request, 0));
		if (!spec.IsSuccess)
		{
			return AgentReply.Failure("invalid", spec.Failure!.Text);
		}

		var line = spec.Value.Render(Argument(request, 1) ?? string.Empty);
		if (!line.IsSuccess)
		{
			return AgentReply.Failure("invalid", line.Failure!.Text);
		}

		if (!await backend.IsActiveAsync(server, ct).ConfigureAwait(false))
		{
			return AgentReply.Failure("conflict", "Сервер остановлен: команду некому выполнить");
		}

		var echo = await backend
			.ConsoleAsync(server, line.Value, spec.Value.ReadsBack, ct)
			.ConfigureAwait(false);

		return Ok(echo);
	}

	/// <summary>
	/// Произвольная строка консоли (ADR-17). Каталога нет, но строка проверяется своей копией
	/// <see cref="RawConsoleLine"/>: одна строка, без управляющих символов, в пределах длины.
	/// Строка уходит в консоль игры, не в оболочку ОС. Кому можно её присылать, решает панель;
	/// демон отвечает только разрешённому uid.
	/// </summary>
	private async Task<AgentReply> RawConsoleAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
	{
		var line = RawConsoleLine.Create(Argument(request, 0));
		if (!line.IsSuccess)
		{
			return AgentReply.Failure("invalid", line.Failure!.Text);
		}

		if (!await backend.IsActiveAsync(server, ct).ConfigureAwait(false))
		{
			return AgentReply.Failure("conflict", "Сервер остановлен: команду некому выполнить");
		}

		return Ok(await backend.ConsoleAsync(server, line.Value.Text, capture: true, ct).ConfigureAwait(false));
	}

	/// <summary>
	/// Действие над игроком. Строку собирает <see cref="PlayerCommand"/> по своей копии правила.
	/// Роль для лишения прав демон читает сам: если бы её присылала панель, можно было бы
	/// назначить любую.
	/// </summary>
	private async Task<AgentReply> PlayerActionAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
	{
		var command = PlayerCommand.Create(Argument(request, 0), Argument(request, 1), Argument(request, 2));
		if (!command.IsSuccess)
		{
			return AgentReply.Failure("invalid", command.Failure!.Text);
		}

		var role = string.Empty;
		if (command.Value.Action == "deop")
		{
			var configured = await DefaultRoleAsync(server, ct).ConfigureAwait(false);
			if (!configured.IsSuccess)
			{
				return AgentReply.Failure("unavailable", configured.Failure!.Text);
			}

			role = configured.Value;
		}

		if (!await backend.IsActiveAsync(server, ct).ConfigureAwait(false))
		{
			return AgentReply.Failure("conflict", "Сервер остановлен: команду некому выполнить");
		}

		await backend.ConsoleAsync(server, command.Value.Render(role), capture: false, ct).ConfigureAwait(false);

		return Ok(new PlayerActionPayload
		{
			Action = command.Value.Action,
			Target = command.Value.IsAnnouncement ? null : command.Value.Player,
			Mode = command.Value.Action == "gamemode" ? command.Value.Detail : null,
		});
	}

	/// <summary>
	/// <c>DefaultRoleCode</c> из <c>serverconfig.json</c>, иначе <c>suplayer</c>.
	/// </summary>
	private static async Task<Result<string>> DefaultRoleAsync(RegisteredServer server, CancellationToken ct)
	{
		JsonNode? config;
		try
		{
			config = JsonNode.Parse(await File.ReadAllTextAsync(server.ConfigFile, ct).ConfigureAwait(false));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return Failure.Unavailable($"Не удалось прочитать роль игроков по умолчанию: {exception.Message}");
		}

		if (config is not JsonObject document)
		{
			return Failure.Unavailable("serverconfig.json не содержит JSON-объекта");
		}

		var role = document["DefaultRoleCode"] is JsonValue value
				   && value.GetValueKind() == JsonValueKind.String
				   && value.GetValue<string>() is { Length: > 0 } code
			? code
			: "suplayer";

		return PlayerCommand.IsValidRole(role)
			? role
			: Failure.Unavailable("DefaultRoleCode в настройках сервера недопустим");
	}

	private static string? Argument(AgentRequest request, int index) =>
		index < request.Args.Count ? request.Args[index] : null;

	private async Task<Result<RegisteredServer>> ResolveAsync(InstanceId id, CancellationToken ct)
	{
		var snapshot = await registry.LoadAsync(ct).ConfigureAwait(false);
		return snapshot switch
		{
			RegistrySnapshot.Unavailable unavailable => Failure.Unavailable(unavailable.Detail),
			RegistrySnapshot.Available available => available.Find(id) is { } server
				? Result.Ok(server)
				: Failure.NotFound($"Неизвестный сервер {id}"),
			_ => Failure.Unavailable("Состояние реестра не распознано"),
		};
	}

	private static string Code(Failure failure) => failure.Code switch
	{
		FailureCode.NotFound => "notfound",
		FailureCode.Invalid => "invalid",
		FailureCode.Conflict => "conflict",
		_ => "unavailable",
	};

	private static AgentReply Ok<T>(T value) =>
		AgentReply.Success(JsonSerializer.SerializeToNode(value, PanelJson.Options));
}
