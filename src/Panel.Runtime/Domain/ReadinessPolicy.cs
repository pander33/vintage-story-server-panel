using Panel.SharedKernel;

namespace Panel.Runtime.Domain;

/// <summary>
/// Решение о готовности сервера. Чистая функция без файловой системы и привилегий; момент
/// проверки передаётся параметром.
/// </summary>
/// <remarks>
/// Правило «без телеметрии проверка ограничена и автоматическое восстановление не
/// разрешается» следует из порядка ветвлений ниже, поэтому порядок важен.
/// </remarks>
public sealed class ReadinessPolicy
{
	public ReadinessVerdict Evaluate(
		ServiceSnapshot service,
		TelemetryReading telemetry,
		WatchdogSettings settings,
		ReadinessVerdict? previous,
		DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(service);
		ArgumentNullException.ThrowIfNull(telemetry);
		ArgumentNullException.ThrowIfNull(settings);

		// 1. Наблюдение выключено: панель не оценивает готовность.
		if (!settings.Enabled)
		{
			return Verdict(HealthState.Disabled, Phrase.From("Watchdog отключён"), service, now);
		}

		// 2. Служба не работает.
		if (!service.IsActive)
		{
			return Verdict(HealthState.Stopped, Phrase.From("Процесс сервера остановлен"), service, now);
		}

		// 3. Готовность подтверждает только свежая телеметрия текущего процесса. Foreign сюда не
		//    проходит: снимок предыдущего процесса ничего не подтверждает.
		if (telemetry is TelemetryReading.Fresh fresh
			&& fresh.Snapshot.OwnershipFor(service, settings.OwnershipTolerance) is not TelemetryOwnership.Foreign)
		{
			return Verdict(HealthState.Ready, Phrase.From("Сервер отвечает, игровая телеметрия актуальна"), service, now);
		}

		// 4. Сервер ещё запускается, и отсутствие телеметрии пока ничего не значит.
		if (service.Uptime < settings.StartupTimeout)
		{
			var seconds = (int) service.Uptime.TotalSeconds;
			return Verdict(HealthState.Starting, Phrase.Format($"Сервер загружается ({seconds} сек.)"), service, now);
		}

		// 5. Телеметрии нет. Degraded не даёт права на восстановление: отсутствие данных не
		//    доказывает зависания.
		if (telemetry is TelemetryReading.Unavailable unavailable)
		{
			var detail = unavailable.Detail is null || string.IsNullOrWhiteSpace(unavailable.Detail.ToString())
				? Phrase.From("Телеметрия недоступна; автоперезапуск запрещён")
				: unavailable.Detail;
			return Verdict(HealthState.Degraded, detail, service, now);
		}

		// 6. Снимок есть, но старше порога: считаем отказы подряд для одного и того же процесса.
		if (telemetry is TelemetryReading.Stale stale && stale.Age >= settings.StaleThreshold)
		{
			var sameProcess = previous is not null && previous.Observed.IsSameProcessAs(service.Identity);
			var failures = sameProcess ? previous!.ConsecutiveFailures + 1 : 1;
			var state = failures >= settings.FailureThreshold ? HealthState.Hung : HealthState.Unresponsive;
			var age = (int) stale.Age.TotalSeconds;
			return Verdict(state, Phrase.Format($"Телеметрия не обновлялась {age} сек."), service, now, failures);
		}

		// 7. Остальное: данных недостаточно для оценки.
		return Verdict(HealthState.Degraded, Phrase.From("Недостаточно данных для проверки"), service, now);
	}

	private static ReadinessVerdict Verdict(
		HealthState state,
		Phrase detail,
		ServiceSnapshot service,
		DateTimeOffset now,
		int failures = 0) => new()
		{
			State = state,
			Detail = detail,
			ConsecutiveFailures = failures,
			Observed = service.Identity,
			ServiceActive = service.IsActive,
			CheckedAt = now,
		};
}
