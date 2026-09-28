using Panel.SharedKernel;

namespace Panel.Runtime.Domain;

/// <summary>
/// Идентичность процесса игры. Одного pid недостаточно, потому что система его
/// переиспользует, поэтому в тройку входят ещё идентификатор загрузки машины и момент
/// старта процесса в тиках.
/// </summary>
/// <remarks>
/// Пустая идентичность — <see cref="Unknown"/>, и с другой пустой она не совпадает. Строковая
/// форма <c>boot_id:pid:start_ticks</c> нужна для формата файлов (ADR-04).
/// </remarks>
public readonly record struct ProcessIdentity(string BootId, int Pid, long StartTicks)
{
	public static ProcessIdentity Unknown { get; } = new(string.Empty, 0, 0);

	public bool IsKnown => Pid != 0 && !string.IsNullOrEmpty(BootId);

	/// <summary>
	/// Две неизвестные идентичности не считаются одним процессом, иначе счётчик отказов
	/// продолжал бы расти после незамеченного перезапуска.
	/// </summary>
	public bool IsSameProcessAs(ProcessIdentity other) =>
		IsKnown && other.IsKnown && this == other;

	public string Serialize() => IsKnown ? string.Concat(BootId, ":", Pid.ToString(), ":", StartTicks.ToString()) : string.Empty;

	public static Result<ProcessIdentity> Parse(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return Unknown;
		}

		var parts = raw.Split(':');
		if (parts.Length != 3
			|| parts[0].Length == 0
			|| !int.TryParse(parts[1], out var pid)
			|| !long.TryParse(parts[2], out var ticks))
		{
			return Failure.Invalid($"Не удалось разобрать идентичность процесса: {raw}");
		}

		return new ProcessIdentity(parts[0], pid, ticks);
	}

	public override string ToString() => IsKnown ? Serialize() : "<unknown>";
}
