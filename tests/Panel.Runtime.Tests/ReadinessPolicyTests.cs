using Panel.Runtime.Domain;
using Panel.SharedKernel;

namespace Panel.Runtime.Tests;

/// <summary>
/// Тесты политики готовности без файловой системы, привилегий и сервера (ADR-14).
/// </summary>
public sealed class ReadinessPolicyTests
{
	private static readonly DateTimeOffset now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

	private readonly ReadinessPolicy policy = new();

	[Fact]
	public void Выключенный_watchdog_не_судит_о_готовности()
	{
		var verdict = policy.Evaluate(
			Running(TimeSpan.FromHours(3)),
			Fresh(now),
			WatchdogSettings.Default with { Enabled = false },
			previous: null,
			now);

		Assert.Equal(HealthState.Disabled, verdict.State);
		Assert.False(verdict.AuthorizesAutomaticRecovery);
	}

	[Fact]
	public void Остановленная_служба_даёт_Stopped()
	{
		var verdict = policy.Evaluate(
			ServiceSnapshot.Stopped with { Unit = "vintagestory.service" },
			new TelemetryReading.Unavailable(Phrase.From("файла нет")),
			WatchdogSettings.Default,
			previous: null,
			now);

		Assert.Equal(HealthState.Stopped, verdict.State);
		Assert.False(verdict.AuthorizesAutomaticRecovery);
	}

	[Fact]
	public void Свежая_телеметрия_текущего_процесса_даёт_Ready()
	{
		var verdict = policy.Evaluate(
			Running(TimeSpan.FromHours(3)),
			Fresh(now),
			WatchdogSettings.Default,
			previous: null,
			now);

		Assert.Equal(HealthState.Ready, verdict.State);
		Assert.False(verdict.IsLimited);
	}

	/// <summary>
	/// Телеметрия предыдущего процесса не подтверждает готовность, даже если она свежая.
	/// </summary>
	[Fact]
	public void Телеметрия_прошлого_процесса_не_считается_подтверждением()
	{
		var service = Running(TimeSpan.FromSeconds(5), startedAt: now);
		var leftover = new TelemetrySnapshot { GeneratedAt = now - TimeSpan.FromMinutes(5) };

		Assert.Equal(
			TelemetryOwnership.Foreign,
			leftover.OwnershipFor(service, WatchdogSettings.Default.OwnershipTolerance));

		var verdict = policy.Evaluate(
			service,
			new TelemetryReading.Fresh(leftover),
			WatchdogSettings.Default,
			previous: null,
			now);

		Assert.NotEqual(HealthState.Ready, verdict.State);
	}

	[Fact]
	public void Недоступная_телеметрия_никогда_не_разрешает_восстановление()
	{
		var verdict = policy.Evaluate(
			Running(TimeSpan.FromHours(3)),
			new TelemetryReading.Unavailable(Phrase.From("файл не найден")),
			WatchdogSettings.Default,
			previous: null,
			now);

		Assert.Equal(HealthState.Degraded, verdict.State);
		Assert.False(verdict.AuthorizesAutomaticRecovery);
		Assert.True(verdict.IsLimited);
		Assert.Equal("файл не найден", verdict.Detail.ToString());
	}

	[Fact]
	public void Недоступная_телеметрия_при_коротком_времени_работы_даёт_Starting()
	{
		var verdict = policy.Evaluate(
			Running(TimeSpan.FromSeconds(30)),
			new TelemetryReading.Unavailable(Phrase.From("мод ещё не записал снимок")),
			WatchdogSettings.Default,
			previous: null,
			now);

		Assert.Equal(HealthState.Starting, verdict.State);
		Assert.False(verdict.AuthorizesAutomaticRecovery);
	}

	[Fact]
	public void Устаревшая_телеметрия_копит_отказы_и_доходит_до_Hung()
	{
		var service = Running(TimeSpan.FromHours(3));
		var stale = new TelemetryReading.Stale(
			new TelemetrySnapshot { GeneratedAt = now - TimeSpan.FromSeconds(120) },
			TimeSpan.FromSeconds(120));

		var first = policy.Evaluate(service, stale, WatchdogSettings.Default, previous: null, now);
		var second = policy.Evaluate(service, stale, WatchdogSettings.Default, first, now);
		var third = policy.Evaluate(service, stale, WatchdogSettings.Default, second, now);

		Assert.Equal(HealthState.Unresponsive, first.State);
		Assert.Equal(1, first.ConsecutiveFailures);
		Assert.Equal(HealthState.Unresponsive, second.State);
		Assert.Equal(HealthState.Hung, third.State);
		Assert.True(third.AuthorizesAutomaticRecovery);
	}

	[Fact]
	public void Счётчик_отказов_обнуляется_при_смене_процесса()
	{
		var stale = new TelemetryReading.Stale(new TelemetrySnapshot(), TimeSpan.FromSeconds(120));
		var settings = WatchdogSettings.Default;

		var first = policy.Evaluate(Running(TimeSpan.FromHours(3), pid: 10), stale, settings, null, now);
		var second = policy.Evaluate(Running(TimeSpan.FromHours(3), pid: 11), stale, settings, first, now);

		Assert.Equal(1, first.ConsecutiveFailures);
		Assert.Equal(1, second.ConsecutiveFailures);
	}

	/// <summary>
	/// Неизвестная идентичность не объединяет два разных процесса, иначе счётчик отказов
	/// сохранился бы после незамеченного перезапуска.
	/// </summary>
	[Fact]
	public void Неизвестные_идентичности_не_считаются_одним_процессом()
	{
		var stale = new TelemetryReading.Stale(new TelemetrySnapshot(), TimeSpan.FromSeconds(120));
		var withoutIdentity = Running(TimeSpan.FromHours(3)) with { Identity = ProcessIdentity.Unknown };

		var first = policy.Evaluate(withoutIdentity, stale, WatchdogSettings.Default, null, now);
		var second = policy.Evaluate(withoutIdentity, stale, WatchdogSettings.Default, first, now);

		Assert.Equal(1, second.ConsecutiveFailures);
	}

	[Fact]
	public void Устаревшая_но_не_дошедшая_до_порога_телеметрия_даёт_Degraded()
	{
		var verdict = policy.Evaluate(
			Running(TimeSpan.FromHours(3)),
			new TelemetryReading.Stale(new TelemetrySnapshot { GeneratedAt = now }, TimeSpan.FromSeconds(20)),
			WatchdogSettings.Default,
			previous: null,
			now);

		Assert.Equal(HealthState.Degraded, verdict.State);
		Assert.Equal(0, verdict.ConsecutiveFailures);
	}

	private static ServiceSnapshot Running(TimeSpan uptime, int pid = 4242, DateTimeOffset? startedAt = null) => new()
	{
		IsActive = true,
		State = RuntimeState.Running,
		StateText = "active",
		SubState = "running",
		Identity = new ProcessIdentity("boot-1", pid, 900),
		Pid = pid,
		StartedAt = startedAt ?? now - uptime,
		Uptime = uptime,
		Unit = "vintagestory.service",
	};

	private static TelemetryReading Fresh(DateTimeOffset generatedAt) =>
		new TelemetryReading.Fresh(new TelemetrySnapshot { GeneratedAt = generatedAt });
}
