namespace Panel.Runtime.Domain;

/// <summary>Игрок в снимке телеметрии.</summary>
public sealed record PlayerSample(string Name, string? Uid, double? PingMilliseconds, string? Address);

/// <summary>
/// Принадлежность телеметрии процессу: три значения, а не булево с умолчанием «принадлежит».
/// </summary>
public enum TelemetryOwnership
{
	/// <summary>Снимок сделан текущим процессом игры.</summary>
	Owned,

	/// <summary>Снимок остался от предыдущего процесса и готовность не подтверждает.</summary>
	Foreign,

	/// <summary>Судить не по чему: нет отметки времени у снимка или у службы.</summary>
	Unknown,
}

public sealed record TelemetrySnapshot
{
	public DateTimeOffset? GeneratedAt { get; init; }

	public double? TicksPerSecond { get; init; }

	public IReadOnlyList<PlayerSample> Players { get; init; } = [];

	/// <summary>
	/// Снимок принадлежит процессу, если сделан не раньше его старта с допуском в секунду. Если
	/// нет какой-либо из двух отметок времени, результат <see cref="TelemetryOwnership.Unknown"/>,
	/// и политика обрабатывает этот случай явно.
	/// </summary>
	public TelemetryOwnership OwnershipFor(ServiceSnapshot service, TimeSpan tolerance)
	{
		if (GeneratedAt is null || service.StartedAt is null)
		{
			return TelemetryOwnership.Unknown;
		}

		return GeneratedAt.Value + tolerance >= service.StartedAt.Value
			? TelemetryOwnership.Owned
			: TelemetryOwnership.Foreign;
	}
}
