using Panel.SharedKernel;

namespace Panel.Runtime.Domain;

/// <summary>
/// Настройки наблюдения. Умолчания и пределы взяты из DEFAULT_WATCHDOG и normalize_watchdog
/// без изменений: значение вне предела отвергается, а не усекается.
/// </summary>
public sealed record WatchdogSettings
{
	public bool Enabled { get; init; } = true;

	/// <summary>Автоперезапуск по умолчанию выключен.</summary>
	public bool AutoRestart { get; init; }

	public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(300);

	public TimeSpan StaleThreshold { get; init; } = TimeSpan.FromSeconds(45);

	public int FailureThreshold { get; init; } = 3;

	public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(900);

	public int MaxRestartsPerHour { get; init; } = 3;

	/// <summary>
	/// Допуск при сопоставлении снимка телеметрии с моментом старта процесса.
	/// </summary>
	public TimeSpan OwnershipTolerance { get; init; } = TimeSpan.FromSeconds(1);

	public static WatchdogSettings Default { get; } = new();

	public Result<WatchdogSettings> Validate()
	{
		(string Key, double Value, double Min, double Max)[] limits =
		[
			("startup_timeout", StartupTimeout.TotalSeconds, 60, 3600),
			("stale_seconds", StaleThreshold.TotalSeconds, 20, 600),
			("failure_threshold", FailureThreshold, 2, 20),
			("cooldown_seconds", Cooldown.TotalSeconds, 300, 86400),
			("max_restarts_hour", MaxRestartsPerHour, 1, 10),
		];

		foreach (var (key, value, min, max) in limits)
		{
			if (value < min || value > max)
			{
				return Failure.Invalid($"Недопустимое значение {key}: требуется {min:0}–{max:0}");
			}
		}

		return this;
	}
}
