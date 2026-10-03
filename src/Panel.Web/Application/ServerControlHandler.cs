using Panel.Configuration.Ports;
using Panel.Contracts.ServerCommands;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.Runtime.Domain;
using Panel.Runtime.Ports;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Сценарии управления сервером: служба, журнал, консоль.
/// </summary>
/// <remarks>
/// Находится в композиционном корне, потому что соединяет Fleet и Runtime, как и
/// <see cref="GetServerStatusHandler"/>. Правила находятся в <see cref="GameServer"/>.
/// </remarks>
public sealed class ServerControlHandler
{
	private static readonly TimeSpan serviceTimeout = TimeSpan.FromSeconds(180);

	private readonly IRegistryStore registry;
	private readonly IServerRuntime runtime;
	private readonly ITelemetryReader telemetry;
	private readonly ReadinessPolicy policy;
	private readonly ReadinessLog log;
	private readonly TimeProvider time;
	private readonly IServerConfigStore configs;
	private readonly IGamePortProbe ports;

	public ServerControlHandler(
		IRegistryStore registry,
		IServerRuntime runtime,
		ITelemetryReader telemetry,
		ReadinessPolicy policy,
		ReadinessLog log,
		TimeProvider time,
		IServerConfigStore configs,
		IGamePortProbe ports)
	{
		this.registry = registry;
		this.runtime = runtime;
		this.telemetry = telemetry;
		this.policy = policy;
		this.log = log;
		this.time = time;
		this.configs = configs;
		this.ports = ports;
	}

	public Task<Result<Done>> ServiceAsync(InstanceId instance, ServiceAction action, CancellationToken ct) =>
		ServiceAsync(instance, action, probePort: false, ct);

	/// <summary>
	/// Действие оператора. Запуск остановленного сервера сначала проверяет игровой порт: занятый
	/// другой программой порт — отказ с причиной, а не упавшая игра. Обслуживание и обновления
	/// запускают сервер без проверки: сразу после остановки порт мог ещё не освободиться.
	/// </summary>
	public Task<Result<Done>> OperatorServiceAsync(InstanceId instance, ServiceAction action, CancellationToken ct) =>
		ServiceAsync(instance, action, probePort: true, ct);

	private async Task<Result<Done>> ServiceAsync(InstanceId instance, ServiceAction action, bool probePort, CancellationToken ct)
	{
		var server = await LoadAsync(instance, ct).ConfigureAwait(false);
		if (!server.IsSuccess)
		{
			return Result.Fail<Done>(server.Failure!);
		}

		var planned = server.Value.PlanService(action);
		if (!planned.IsSuccess)
		{
			return Result.Fail<Done>(planned.Failure!);
		}

		if (probePort && planned.Value == ServiceAction.Start && !server.Value.Service.IsActive
			&& await GameEndpoint.ReadAsync(configs, instance, ct).ConfigureAwait(false) is { } endpoint)
		{
			var port = endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
			switch (ports.Probe(endpoint.Address, endpoint.Port))
			{
				case PortAvailability.InUse:
					return Failure.Conflict($"Порт {port} уже занят другой программой, и сервер не сможет его открыть. Остановите её или смените Port в настройках сервера");
				case PortAvailability.Denied:
					return Failure.Conflict($"Система не даёт занять порт {port}: на Windows это бывает с портами, зарезервированными Hyper-V (netsh interface ipv4 show excludedportrange protocol=tcp). Смените Port в настройках сервера");
			}
		}

		return await runtime
			.ServiceAsync(server.Value.Id, planned.Value, serviceTimeout, ct)
			.ConfigureAwait(false);
	}

	public async Task<Result<ConsoleEcho>> SendAsync(
		InstanceId instance, ServerCommandRequest request, CancellationToken ct)
	{
		var server = await LoadAsync(instance, ct).ConfigureAwait(false);
		if (!server.IsSuccess)
		{
			return Result.Fail<ConsoleEcho>(server.Failure!);
		}

		var dispatch = server.Value.PlanCommand(request);
		if (!dispatch.IsSuccess)
		{
			return Result.Fail<ConsoleEcho>(dispatch.Failure!);
		}

		return await runtime.SendAsync(server.Value.Id, dispatch.Value, ct).ConfigureAwait(false);
	}

	/// <summary>Произвольная строка консоли; правила в <see cref="GameServer.PlanRawCommand"/>.</summary>
	public async Task<Result<ConsoleEcho>> SendRawAsync(InstanceId instance, RawConsoleLine line, CancellationToken ct)
	{
		var server = await LoadAsync(instance, ct).ConfigureAwait(false);
		if (!server.IsSuccess)
		{
			return Result.Fail<ConsoleEcho>(server.Failure!);
		}

		var dispatch = server.Value.PlanRawCommand(line);
		if (!dispatch.IsSuccess)
		{
			return Result.Fail<ConsoleEcho>(dispatch.Failure!);
		}

		return await runtime.SendAsync(server.Value.Id, dispatch.Value, ct).ConfigureAwait(false);
	}

	public async Task<Result<IReadOnlyList<LogEntry>>> LogsAsync(
		InstanceId instance, int count, CancellationToken ct)
	{
		var resolved = await ResolveAsync(instance, ct).ConfigureAwait(false);
		return resolved.IsSuccess
			? await runtime.LogsAsync(resolved.Value.Id, count, ct).ConfigureAwait(false)
			: Result.Fail<IReadOnlyList<LogEntry>>(resolved.Failure!);
	}

	public async Task<Result<LogPage>> LogsFromAsync(InstanceId instance, LogCursor cursor, CancellationToken ct)
	{
		var resolved = await ResolveAsync(instance, ct).ConfigureAwait(false);
		return resolved.IsSuccess
			? await runtime.LogsFromAsync(resolved.Value.Id, cursor, ct).ConfigureAwait(false)
			: Result.Fail<LogPage>(resolved.Failure!);
	}

	/// <summary>
	/// Собрать агрегат: снимок службы, доступность консоли и вердикт готовности. Три обращения к
	/// демону, потому что для решения нужны все три.
	/// </summary>
	public async Task<Result<GameServer>> LoadAsync(InstanceId instance, CancellationToken ct)
	{
		var resolved = await ResolveAsync(instance, ct).ConfigureAwait(false);
		if (!resolved.IsSuccess)
		{
			return Result.Fail<GameServer>(resolved.Failure!);
		}

		var id = resolved.Value.Id;
		var snapshot = await runtime.StatusAsync(id, ct).ConfigureAwait(false);
		if (!snapshot.IsSuccess)
		{
			return Result.Fail<GameServer>(snapshot.Failure!);
		}

		var telemetry = await this.telemetry.ReadAsync(id, ct).ConfigureAwait(false);
		// Вердикт нужен для решения и в цепочку не записывается: её ведёт только такт наблюдателя,
		// иначе каждое действие считалось бы лишней проверкой.
		var verdict = policy.Evaluate(
			snapshot.Value, telemetry, log.Settings(id), log.Previous(id), time.GetUtcNow());

		var console = await runtime.CapabilitiesAsync(id, ct).ConfigureAwait(false);

		// Недоступная консоль не мешает собрать агрегат: сервером можно управлять и без неё. Отказ
		// превращается в «консоль недоступна» с причиной.
		var capability = console.IsSuccess
			? console.Value
			: new ConsoleCapability(ConsoleTransport.Disabled, false, console.Failure!.Text);

		return new GameServer(id, snapshot.Value, capability, verdict);
	}

	/// <summary>
	/// Какой сервер имеется в виду. Неизвестное имя не ошибка: вкладка могла устареть.
	/// </summary>
	private async Task<Result<RegisteredServer>> ResolveAsync(InstanceId requested, CancellationToken ct)
	{
		var snapshot = await registry.LoadAsync(ct).ConfigureAwait(false);
		if (snapshot is RegistrySnapshot.Unavailable unavailable)
		{
			return Failure.Unavailable(unavailable.Detail);
		}

		var available = (RegistrySnapshot.Available) snapshot;
		var server = available.Find(requested) ?? available.EffectiveDefault;

		return server is not null
			? Result.Ok(server)
			: Failure.NotFound("Панель не знает ни одного сервера");
	}
}
