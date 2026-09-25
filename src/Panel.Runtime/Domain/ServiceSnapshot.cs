namespace Panel.Runtime.Domain;

/// <summary>Состояние службы, видимое снаружи процесса.</summary>
public enum RuntimeState
{
	Unknown,
	Stopped,
	Starting,
	Running,
	Failed,
}

/// <summary>
/// Снимок службы. О телеметрии и готовности ничего не знает: решение принимает
/// <see cref="ReadinessPolicy"/>.
/// </summary>
public sealed record ServiceSnapshot
{
	public required bool IsActive { get; init; }

	public required RuntimeState State { get; init; }

	/// <summary>У systemd running/exited/…; у supervised свой набор значений.</summary>
	public required string SubState { get; init; }

	/// <summary>
	/// ActiveState строкой, без разбора. <see cref="State"/> сводит несколько значений systemd в
	/// одно, а контракт отдаёт исходное, и из перечисления reloading восстановился бы как
	/// activating.
	/// </summary>
	public string StateText { get; init; } = "unknown";

	public required ProcessIdentity Identity { get; init; }

	/// <summary>
	/// MainPID службы, отдельно от <see cref="Identity"/>: systemd знает pid и тогда, когда /proc
	/// уже ничего не отдал (процесс завершился между двумя чтениями). Идентичность в этом случае
	/// неизвестна, а pid известен.
	/// </summary>
	public int Pid { get; init; }

	public DateTimeOffset? StartedAt { get; init; }

	public TimeSpan Uptime { get; init; }

	public long MemoryBytes { get; init; }

	public long CpuNanoseconds { get; init; }

	public int RestartCount { get; init; }

	/// <summary>Строка systemd ExecMainStartTimestamp в том виде, в каком её показывает интерфейс.</summary>
	public string StartedText { get; init; } = string.Empty;

	public string Unit { get; init; } = string.Empty;

	/// <summary>
	/// Снимок остановленной службы. Имя юнита сюда не передаётся: вызывающий знает его из записи
	/// реестра, а голый string в доменных методах запрещён архитектурным тестом.
	/// </summary>
	public static ServiceSnapshot Stopped { get; } = new()
	{
		IsActive = false,
		State = RuntimeState.Stopped,
		SubState = "dead",
		StateText = "inactive",
		Identity = ProcessIdentity.Unknown,
	};

	/// <summary>
	/// Разбор ActiveState systemd. SubState хранится отдельно: active/running и active/exited для
	/// игрового сервера означают разное.
	/// </summary>
	public static RuntimeState ParseState(string? activeState) => activeState switch
	{
		"active" => RuntimeState.Running,
		"activating" or "reloading" => RuntimeState.Starting,
		"inactive" or "deactivating" => RuntimeState.Stopped,
		"failed" => RuntimeState.Failed,
		_ => RuntimeState.Unknown,
	};
}
