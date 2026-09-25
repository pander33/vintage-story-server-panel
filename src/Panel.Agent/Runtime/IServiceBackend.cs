using Panel.Contracts.Agent;
using Panel.Fleet.Domain;

namespace Panel.Agent.Runtime;

/// <summary>
/// Как демон запускает сервер, читает его журнал и пишет в консоль. Две реализации: юнит
/// systemd и дочерний процесс демона (Windows).
/// </summary>
/// <remarks>
/// Допустимость команды решает таблица операций; сюда приходит уже собранная и проверенная
/// строка.
/// </remarks>
public interface IServiceBackend
{
	/// <summary><c>systemd</c> | <c>supervised</c>.</summary>
	string Name { get; }

	Task<ServiceStatusPayload> StatusAsync(RegisteredServer server, CancellationToken ct);

	Task<bool> IsActiveAsync(RegisteredServer server, CancellationToken ct);

	/// <summary><c>start</c> | <c>stop</c> | <c>restart</c>; при отказе бросает исключение ввода-вывода.</summary>
	Task ServiceAsync(RegisteredServer server, string action, TimeSpan timeout, CancellationToken ct);

	Task<IReadOnlyList<LogEntryPayload>> LogsAsync(RegisteredServer server, int count, CancellationToken ct);

	/// <summary>
	/// Всё, что появилось после курсора. Для панели курсор непрозрачен: у systemd это строка
	/// journald, у supervised — смещение в байтах в журнале консоли.
	/// </summary>
	Task<LogPagePayload> LogsFromAsync(RegisteredServer server, string cursor, CancellationToken ct);

	/// <summary>
	/// Отправить строку в консоль и при необходимости прочитать ответ. Курсор снимается внутри,
	/// до записи, чтобы вызывающий не мог переставить шаги.
	/// </summary>
	Task<ConsoleEchoPayload> ConsoleAsync(RegisteredServer server, string line, bool capture, CancellationToken ct);

	Task<ConsoleCapabilityPayload> CapabilitiesAsync(RegisteredServer server, CancellationToken ct);
}
