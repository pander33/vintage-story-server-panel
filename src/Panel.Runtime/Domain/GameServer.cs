using Panel.Contracts.ServerCommands;
using Panel.SharedKernel;

namespace Panel.Runtime.Domain;

public enum ServiceAction
{
	Start,
	Stop,
	Restart,
}

/// <summary>
/// Проверенное намерение отправить команду.
/// </summary>
/// <remarks>
/// Содержит и собранную строку, и исходную пару «действие + значение». Строка нужна журналу и
/// интерфейсу, пара уходит демону: он собирает команду сам по своей копии каталога и
/// присланную строку не исполняет.
///
/// Произвольная строка помечена <c>IsRaw</c>: демон не собирает её по каталогу, а
/// проверяет своей копией <see cref="RawConsoleLine"/> и пишет как есть.
/// </remarks>
public sealed record ConsoleDispatch(string Line, CaptureMode Capture, string Action, string Value, bool IsRaw = false);

/// <summary>
/// Игровой сервер как агрегат: состояние службы, доступность консоли и вердикт готовности в
/// одном месте, которое решает, что с сервером можно делать.
/// </summary>
/// <remarks>
/// Агрегат ничего не исполняет: он возвращает план (проверенное намерение), а исполняет порт.
/// Строку консоли можно получить только через <see cref="PlanCommand"/>, поэтому проверку, что
/// сервер работает, не обойти.
/// </remarks>
public sealed class GameServer
{
	public GameServer(
		InstanceId id,
		ServiceSnapshot service,
		ConsoleCapability console,
		ReadinessVerdict readiness)
	{
		Id = id;
		Service = service;
		Console = console;
		Readiness = readiness;
	}

	public InstanceId Id { get; }

	public ServiceSnapshot Service { get; }

	public ConsoleCapability Console { get; }

	public ReadinessVerdict Readiness { get; }

	/// <summary>
	/// Запуск остановленного и остановка запущенного сервера не ошибка: systemd идемпотентен.
	/// Перезапуск остановленного сервера равен запуску.
	/// </summary>
	public Result<ServiceAction> PlanService(ServiceAction action) => action;

	public Result<ConsoleDispatch> PlanCommand(ServerCommandRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);

		if (!Service.IsActive)
		{
			return Failure.Conflict("Сервер остановлен: команду некому выполнить");
		}

		if (!Console.IsAvailable)
		{
			return Failure.Conflict(Console.UnavailableReason ?? Phrase.From("Консоль недоступна"));
		}

		var spec = ServerCommandCatalogue.Find(request.Action);
		if (!spec.IsSuccess)
		{
			return Result.Fail<ConsoleDispatch>(spec.Failure!);
		}

		return spec.Value.Render(request.Value).Map(line => new ConsoleDispatch(
			line,
			spec.Value.ReadsBack ? CaptureMode.ReadBack : CaptureMode.None,
			spec.Value.Action,
			request.Value));
	}

	/// <summary>
	/// Произвольная строка консоли. Условия те же, что для каталога: сервер работает и
	/// консоль доступна. Ответ читается всегда: человек, набравший команду вручную, ждёт увидеть
	/// ответ сервера.
	/// </summary>
	public Result<ConsoleDispatch> PlanRawCommand(RawConsoleLine line)
	{
		ArgumentNullException.ThrowIfNull(line);

		if (!Service.IsActive)
		{
			return Failure.Conflict("Сервер остановлен: команду некому выполнить");
		}

		if (!Console.IsAvailable)
		{
			return Failure.Conflict(Console.UnavailableReason ?? Phrase.From("Консоль недоступна"));
		}

		return new ConsoleDispatch(line.Text, CaptureMode.ReadBack, string.Empty, line.Text, IsRaw: true);
	}
}
