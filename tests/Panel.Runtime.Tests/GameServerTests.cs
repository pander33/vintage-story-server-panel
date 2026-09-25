using Panel.Contracts.ServerCommands;
using Panel.Runtime.Domain;
using Panel.SharedKernel;

namespace Panel.Runtime.Tests;

public sealed class GameServerTests
{
	private static readonly DateTimeOffset now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

	/// <summary>
	/// Команду остановленному серверу отвергает агрегат.
	/// </summary>
	[Fact]
	public void Остановленный_сервер_команд_не_принимает()
	{
		var result = Server(active: false).PlanCommand(new ServerCommandRequest("stats"));

		Assert.False(result.IsSuccess);
		Assert.Equal(FailureCode.Conflict, result.Failure!.Code);
	}

	[Fact]
	public void Недоступная_консоль_называет_причину()
	{
		var server = Server(console: new ConsoleCapability(
			ConsoleTransport.SupervisedStdin, false, "Консоль потеряна до перезапуска сервера"));

		var result = server.PlanCommand(new ServerCommandRequest("stats"));

		Assert.False(result.IsSuccess);
		Assert.Equal("Консоль потеряна до перезапуска сервера", result.Failure!.Message);
	}

	[Fact]
	public void Неизвестное_действие_отвергается() =>
		Assert.False(Server().PlanCommand(new ServerCommandRequest("rm -rf")).IsSuccess);

	[Fact]
	public void Команда_без_значения_разворачивается_в_свою_строку()
	{
		var dispatch = Server().PlanCommand(new ServerCommandRequest("autosave-now")).Value;

		// /autosavenow одним словом: «/autosave now» сервер не распознает.
		Assert.Equal("/autosavenow", dispatch.Line);
		Assert.Equal(CaptureMode.ReadBack, dispatch.Capture);
	}

	[Fact]
	public void Лишнее_значение_у_команды_без_параметра_отвергается() =>
		Assert.False(Server().PlanCommand(new ServerCommandRequest("time-stop", "12")).IsSuccess);

	[Fact]
	public void План_несёт_и_строку_и_исходную_пару_для_провода()
	{
		var dispatch = Server().PlanCommand(new ServerCommandRequest("time-set", "midnight")).Value;

		Assert.Equal("/time set midnight", dispatch.Line);
		Assert.Equal("time-set", dispatch.Action);
		Assert.Equal("midnight", dispatch.Value);
	}

	/// <summary>ADR-17: произвольная строка передаётся как есть, с чтением ответа и отдельной меткой.</summary>
	[Fact]
	public void Произвольная_строка_уходит_как_есть_и_с_ответом()
	{
		var dispatch = Server().PlanRawCommand(RawConsoleLine.Create("/weather set clear").Value).Value;

		Assert.True(dispatch.IsRaw);
		Assert.Equal("/weather set clear", dispatch.Line);
		Assert.Equal(CaptureMode.ReadBack, dispatch.Capture);
	}

	[Fact]
	public void Произвольная_строка_подчиняется_тем_же_условиям_что_каталог()
	{
		var line = RawConsoleLine.Create("/stats").Value;
		var lost = Server(console: new ConsoleCapability(ConsoleTransport.SupervisedStdin, false, "Консоль потеряна"));

		Assert.Equal(FailureCode.Conflict, Server(active: false).PlanRawCommand(line).Failure!.Code);
		Assert.Equal("Консоль потеряна", lost.PlanRawCommand(line).Failure!.Message);
	}

	[Fact]
	public void Действия_службы_идемпотентны_и_не_проверяют_состояние()
	{
		// systemd идемпотентен: остановка остановленного сервера не ошибка.
		Assert.True(Server(active: false).PlanService(ServiceAction.Stop).IsSuccess);
		Assert.True(Server().PlanService(ServiceAction.Start).IsSuccess);
	}

	private static GameServer Server(bool active = true, ConsoleCapability? console = null) => new(
		InstanceId.Parse("main"),
		new ServiceSnapshot
		{
			IsActive = active,
			State = active ? RuntimeState.Running : RuntimeState.Stopped,
			StateText = active ? "active" : "inactive",
			SubState = active ? "running" : "dead",
			Identity = new ProcessIdentity("boot", 1, 2),
		},
		console ?? new ConsoleCapability(ConsoleTransport.Fifo, true, null),
		ReadinessVerdict.NotCheckedYet(now));
}
