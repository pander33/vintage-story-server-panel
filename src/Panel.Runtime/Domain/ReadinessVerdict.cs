using Panel.SharedKernel;

namespace Panel.Runtime.Domain;

public enum HealthState
{
	Unknown,
	Disabled,
	Stopped,
	Starting,
	Ready,
	Degraded,
	Unresponsive,
	Hung,
}

/// <summary>
/// Вердикт готовности. Право на автоматическое восстановление — свойство вердикта, а не
/// отдельное решение наблюдателя.
/// </summary>
/// <remarks>
/// WatchdogService не видит <see cref="State"/> и спрашивает только
/// <see cref="AuthorizesAutomaticRecovery"/>, поэтому degraded и starting не могут случайно
/// дать право на перезапуск.
/// </remarks>
public sealed record ReadinessVerdict
{
	public required HealthState State { get; init; }

	/// <summary>Пояснение для интерфейса.</summary>
	public required Phrase Detail { get; init; }

	public int ConsecutiveFailures { get; init; }

	public ProcessIdentity Observed { get; init; } = ProcessIdentity.Unknown;

	public bool ServiceActive { get; init; }

	public required DateTimeOffset CheckedAt { get; init; }

	/// <summary>Единственное место в панели, где решается, можно ли перезапустить сервер.</summary>
	public bool AuthorizesAutomaticRecovery => State is HealthState.Hung;

	/// <summary>«Ограниченная проверка»: данных недостаточно для оценки.</summary>
	public bool IsLimited => State is HealthState.Degraded
		or HealthState.Unresponsive
		or HealthState.Unknown;

	/// <summary>
	/// Вердикт, который панель показывает до первой проверки.
	/// </summary>
	public static ReadinessVerdict NotCheckedYet(DateTimeOffset now) => new()
	{
		State = HealthState.Unknown,
		Detail = Phrase.From("Проверка ещё не выполнялась"),
		CheckedAt = now,
	};
}

/// <summary>
/// Имена состояний в протоколе (ADR-16): фронтенд сравнивает их со строками и раскрашивает
/// плашку.
/// </summary>
public static class HealthStateWire
{
	public static string ToWire(this HealthState state) => state switch
	{
		HealthState.Disabled => "disabled",
		HealthState.Stopped => "stopped",
		HealthState.Starting => "starting",
		HealthState.Ready => "ready",
		HealthState.Degraded => "degraded",
		HealthState.Unresponsive => "unresponsive",
		HealthState.Hung => "hung",
		_ => "unknown",
	};

	public static HealthState FromWire(string? wire) => wire switch
	{
		"disabled" => HealthState.Disabled,
		"stopped" => HealthState.Stopped,
		"starting" => HealthState.Starting,
		"ready" => HealthState.Ready,
		"degraded" => HealthState.Degraded,
		"unresponsive" => HealthState.Unresponsive,
		"hung" => HealthState.Hung,
		_ => HealthState.Unknown,
	};
}
