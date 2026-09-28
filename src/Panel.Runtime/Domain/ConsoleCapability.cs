using Panel.SharedKernel;

namespace Panel.Runtime.Domain;

/// <summary>Чем панель пишет в консоль сервера.</summary>
public enum ConsoleTransport
{
	/// <summary>Консоли нет. Это умолчание: консоль включается явно.</summary>
	Disabled,

	/// <summary>Именованный канал, созданный <c>vs-console-wrapper</c> от root.</summary>
	Fifo,

	Screen,

	/// <summary><c>server.sh</c> самой игры.</summary>
	ServerSh,

	/// <summary>stdin дочернего процесса, которым владеет служба Windows.</summary>
	SupervisedStdin,
}

/// <summary>
/// Доступна ли консоль сейчас и если нет, то почему.
/// </summary>
/// <remarks>
/// Нужно для случая, которого нет на Linux: служба Windows после собственного перезапуска
/// находит дочерний процесс по pid и времени создания, но его stdin восстановить нельзя.
/// Лучше заранее сказать, что консоль недоступна до перезапуска сервера, чем отказать
/// посреди операции.
///
/// Это поле записи, поэтому вторая реализация порта не может о нём забыть.
/// </remarks>
public sealed record ConsoleCapability(
	ConsoleTransport Transport,
	bool IsAvailable,
	Phrase? UnavailableReason)
{
	public static ConsoleCapability Off { get; } =
		new(ConsoleTransport.Disabled, false, Phrase.From("Консоль не настроена для этого сервера"));

	public static ConsoleTransport ParseTransport(string? configured) => configured switch
	{
		"fifo" => ConsoleTransport.Fifo,
		"screen" => ConsoleTransport.Screen,
		"server.sh" => ConsoleTransport.ServerSh,
		"supervised" => ConsoleTransport.SupervisedStdin,
		_ => ConsoleTransport.Disabled,
	};

	/// <summary>
	/// Имя транспорта в том виде, в каком оно записано в реестре; обратное к
	/// <see cref="ParseTransport"/>. <c>ToString()</c> перечисления дало бы <c>serversh</c> вместо
	/// <c>server.sh</c>.
	/// </summary>
	public static string ToWire(ConsoleTransport transport) => transport switch
	{
		ConsoleTransport.Fifo => "fifo",
		ConsoleTransport.Screen => "screen",
		ConsoleTransport.ServerSh => "server.sh",
		ConsoleTransport.SupervisedStdin => "supervised",
		_ => "disabled",
	};
}

/// <summary>Что вернулось из журнала после отправленной команды.</summary>
public sealed record ConsoleEcho(IReadOnlyList<string> Lines, TimeSpan Waited)
{
	public static ConsoleEcho Silent { get; } = new([], TimeSpan.Zero);
}

/// <summary>Читать ли ответ обратно из журнала.</summary>
public enum CaptureMode
{
	None,
	ReadBack,
}
