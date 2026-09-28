using Panel.Contracts.Http;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.Runtime.Domain;
using Panel.Runtime.Ports;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Обработка GET /api/status: реестр → служба → телеметрия → вердикт → ответ.
/// </summary>
/// <remarks>
/// Обработчику нужен IRegistryStore из контекста Fleet, а контекст Runtime не может ссылаться
/// на другой контекст (ADR-01), поэтому обработчик находится в композиционном корне.
/// </remarks>
public sealed class GetServerStatusHandler
{
	private readonly IRegistryStore registry;
	private readonly IServerRuntime runtime;
	private readonly ITelemetryReader telemetry;
	private readonly ReadinessPolicy policy;
	private readonly ReadinessLog log;
	private readonly TimeProvider time;
	private readonly MaintenanceCoordinator coordinator;

	public GetServerStatusHandler(
		IRegistryStore registry,
		IServerRuntime runtime,
		ITelemetryReader telemetry,
		ReadinessPolicy policy,
		ReadinessLog log,
		TimeProvider time,
		MaintenanceCoordinator coordinator)
	{
		this.registry = registry;
		this.runtime = runtime;
		this.telemetry = telemetry;
		this.policy = policy;
		this.log = log;
		this.time = time;
		this.coordinator = coordinator;
	}

	public async Task<Result<StatusResponse>> HandleAsync(GetServerStatus query, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(query);

		var server = await ResolveAsync(query.Instance, ct).ConfigureAwait(false);
		if (!server.IsSuccess)
		{
			return Result.Fail<StatusResponse>(server.Failure!);
		}

		var instance = server.Value.Id;
		var snapshot = await runtime.StatusAsync(instance, ct).ConfigureAwait(false);
		if (!snapshot.IsSuccess)
		{
			return Result.Fail<StatusResponse>(snapshot.Failure!);
		}

		// Здоровье — последний вердикт наблюдателя, а не новый, иначе каждое обновление страницы
		// считалось бы ещё одной неудачей подряд. Пока такт ни разу не проверил сервер, вердикт
		// вычисляется на месте и не записывается.
		var verdict = log.Previous(instance);
		if (verdict is null)
		{
			var telemetry = await this.telemetry.ReadAsync(instance, ct).ConfigureAwait(false);
			verdict = policy.Evaluate(
				snapshot.Value,
				telemetry,
				log.Settings(instance),
				previous: null,
				time.GetUtcNow());
		}

		var lease = coordinator.Current(instance);
		return Compose(snapshot.Value, verdict, log.RecoveryBlocked(instance), lease);
	}

	/// <summary>
	/// Какой сервер имеется в виду. Неизвестное имя не ошибка: вкладка могла устареть.
	/// </summary>
	private async Task<Result<RegisteredServer>> ResolveAsync(InstanceId requested, CancellationToken ct)
	{
		var snapshot = await registry.LoadAsync(ct).ConfigureAwait(false);

		if (snapshot is RegistrySnapshot.Unavailable unavailable)
		{
			// Недоступный реестр — отказ, а не пустой список: «реестра нет» и «серверов нет» —
			// разные состояния.
			return Failure.Unavailable(unavailable.Detail);
		}

		var available = (RegistrySnapshot.Available) snapshot;
		var server = available.Find(requested) ?? available.EffectiveDefault;

		return server is not null
			? Result.Ok(server)
			: Failure.NotFound("Панель не знает ни одного сервера");
	}

	internal static StatusResponse Compose(
		ServiceSnapshot service,
		ReadinessVerdict verdict,
		Phrase? recoveryBlocked = null,
		Panel.Maintenance.Domain.MaintenanceLease? lease = null) => new()
		{
			Active = service.IsActive,
			State = service.StateText,
			SubState = service.SubState,
			Pid = service.Pid,
			Memory = service.MemoryBytes,
			CpuNs = service.CpuNanoseconds,
			Restarts = service.RestartCount,
			Uptime = (long) service.Uptime.TotalSeconds,
			Started = service.StartedText,
			StartedAt = service.StartedAt is { } moment ? UnixTime.ToSeconds(moment) : 0,
			Instance = service.Identity.Serialize(),
			Unit = service.Unit,
			Health = new HealthResponse
			{
				CheckedAt = verdict.CheckedAt.ToUnixTimeSeconds(),
				Instance = verdict.Observed.Serialize(),
				Active = verdict.ServiceActive,
				State = verdict.State.ToWire(),
				Detail = verdict.Detail,
				Failures = verdict.ConsecutiveFailures,
				RecoveryBlocked = recoveryBlocked,
			},
			Maintenance = lease is null ? null : new MaintenanceStatus
			{
				Purpose = Panel.Maintenance.Domain.LeasePurposeWire.ToWire(lease.Purpose),
				Title = LeasePurposeTitles.ToTitle(lease.Purpose),
				StartedAt = lease.AcquiredAt,
			},
		};
}
