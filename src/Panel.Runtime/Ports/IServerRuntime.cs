using Panel.Runtime.Domain;
using Panel.SharedKernel;

namespace Panel.Runtime.Ports;

/// <summary>
/// Как панель видит службу игрового сервера и обращается к ней.
/// </summary>
/// <remarks>
/// Порт узкий: общий интерфейс привилегированной стороны с десятками методов привязал
/// бы домен к привилегированному вызову.
///
/// Порт ничего не решает. Допустимость команды проверяет <see cref="GameServer"/>, сюда
/// приходит уже проверенный план.
/// </remarks>
public interface IServerRuntime
{
	/// <summary>systemd | supervised: какая реализация обслуживает этот сервер.</summary>
	string Name { get; }

	Task<Result<ServiceSnapshot>> StatusAsync(InstanceId instance, CancellationToken ct);

	Task<Result<Done>> ServiceAsync(InstanceId instance, ServiceAction action, TimeSpan timeout, CancellationToken ct);

	/// <summary>Последние строки журнала службы.</summary>
	Task<Result<IReadOnlyList<LogEntry>>> LogsAsync(InstanceId instance, int count, CancellationToken ct);

	/// <summary>Всё, что появилось после курсора, и новый курсор.</summary>
	Task<Result<LogPage>> LogsFromAsync(InstanceId instance, LogCursor cursor, CancellationToken ct);

	/// <summary>
	/// Отправить строку в консоль. Курсор снимается внутри, до записи: ответ читается только из
	/// журнала, и вызывающий не может переставить эти два шага.
	/// </summary>
	Task<Result<ConsoleEcho>> SendAsync(InstanceId instance, ConsoleDispatch dispatch, CancellationToken ct);

	Task<Result<ConsoleCapability>> CapabilitiesAsync(InstanceId instance, CancellationToken ct);
}
