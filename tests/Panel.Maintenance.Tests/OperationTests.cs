using System.Text.Json.Nodes;
using Panel.Maintenance.Domain;
using Panel.SharedKernel;

namespace Panel.Maintenance.Tests;

/// <summary>Агрегат операции (детализация Maintenance §3) и форма записи.</summary>
public sealed class OperationTests
{
	private static readonly OperationId id = OperationId.Parse("0123456789abcdef0123456789abcdef")!.Value;

	[Fact]
	public void Прерванная_операция_не_переписывает_завершённую()
	{
		var operation = Operation.Start(id, OperationKind.Mods, 100);
		operation.Succeed(200);

		Assert.Throws<InvalidOperationException>(() => operation.MarkInterrupted(300));
		Assert.Throws<InvalidOperationException>(() => operation.Fail(Failure.Unavailable("x"), 300));
		Assert.Throws<InvalidOperationException>(() => operation.Record(new OperationStep(StepAction.Backup, StepResult.Ok)));
		Assert.Equal(200, operation.FinishedAt);
	}

	[Fact]
	public void Итог_модов_считает_установленные()
	{
		var operation = Operation.Start(id, OperationKind.Mods, 100);
		operation.Record(new OperationStep(StepAction.Download, StepResult.Ok) { Count = 2 });
		operation.Record(new OperationStep(StepAction.Install, StepResult.Ok) { ModId = "a", Version = "1.0" });
		operation.Record(new OperationStep(StepAction.Install, StepResult.Ok) { ModId = "b", Version = null });
		operation.Succeed(200);

		Assert.Equal("Обновлено модов: 2", operation.Detail?.ToString());
		var steps = operation.ToJson()["steps"]!.AsArray();
		Assert.Equal(["action", "status", "count"], steps[0]!.AsObject().Select(pair => pair.Key));
		Assert.Equal(["action", "status", "modid", "version"], steps[2]!.AsObject().Select(pair => pair.Key));
		Assert.Null(steps[2]!["version"]);
	}

	[Fact]
	public void Запись_обновления_игры_в_замороженной_форме()
	{
		var operation = Operation.CreateGameUpdate(id, "1.22.7", "1.22.8", 100);
		operation.Enter(OperationPhase.GamePackageInspect);
		operation.Record(new OperationStep(StepAction.Validate, StepResult.Ok) { Files = 10 });
		operation.NoteDataSnapshot(SnapshotName.Create("vintagestory-2026.tar.gz").Value);
		operation.NoteProgramSnapshot(SnapshotName.Create("server-program-2026.tar.gz").Value);
		operation.Record(new OperationStep(StepAction.Install, StepResult.Ok) { Snapshot = "server-program-2026.tar.gz" });
		operation.Enter(OperationPhase.PanelGameVersion);
		operation.NotePanelRestart(false);

		var running = operation.ToJson();
		Assert.Equal(
			["id", "type", "title", "started_at", "status", "steps", "previous_version", "target_version",
			 "phase", "data_snapshot", "program_snapshot", "panel_restart_required"],
			running.Select(pair => pair.Key));
		Assert.Equal("0123456789abcdef0123456789abcdef", running["id"]!.GetValue<string>());
		Assert.Equal("game", running["type"]!.GetValue<string>());
		Assert.Equal("Обновление Vintage Story Server", running["title"].Said());
		Assert.Equal("running", running["status"]!.GetValue<string>());
		Assert.Equal("panel-game-version", running["phase"]!.GetValue<string>());
		Assert.Equal(["action", "status", "snapshot"], running["steps"]![1]!.AsObject().Select(pair => pair.Key));

		operation.Fail(Failure.Unavailable("Сервер не подтвердил готовность после обновления"), 150);
		var finished = operation.ToJson();
		Assert.Equal("failed", finished["status"]!.GetValue<string>());
		Assert.Equal("Сервер не подтвердил готовность после обновления", finished["detail"].Said());
		Assert.Equal(150, finished["finished_at"]!.GetValue<long>());
		Assert.Equal(100, finished["ts"]!.GetValue<long>());
	}

	[Fact]
	public void Обновление_игры_без_версий_не_создаётся()
	{
		Assert.Throws<ArgumentException>(() => Operation.Start(id, OperationKind.Game, 1));
	}

	[Fact]
	public void Отказ_до_начала_записывается_как_preflight()
	{
		var operation = Operation.Start(id, OperationKind.Preflight, 5);
		operation.Fail(Failure.Invalid("Для этого снимка нет связанной offline-копии данных; автоматический откат запрещён"), 6);

		var record = operation.ToJson();
		Assert.Equal("preflight", record["type"]!.GetValue<string>());
		Assert.Equal("Обновление не началось", record["title"].Said());
		Assert.Empty(record["steps"]!.AsArray());
	}

	[Fact]
	public void Незавершённая_запись_становится_прерванной_с_сохранением_полей()
	{
		var current = new JsonObject { ["id"] = "old", ["title"] = "Update", ["started_at"] = 100, ["steps"] = new JsonArray(), ["extra"] = 1 };

		var record = OperationRecovery.Interrupt(current, 500)!;

		Assert.Equal("interrupted", record["status"]!.GetValue<string>());
		Assert.Equal(OperationRecovery.InterruptedDetail, PhraseJsonConverter.FromNode(record["detail"]));
		Assert.Equal(500, record["finished_at"]!.GetValue<long>());
		Assert.Equal(100, record["ts"]!.GetValue<long>());
		Assert.Equal(1, record["extra"]!.GetValue<int>());
		Assert.Null(current["status"]);
	}

	[Theory]
	[InlineData("success")]
	[InlineData("failed")]
	[InlineData("interrupted")]
	public void Завершённая_запись_не_переписывается(string status)
	{
		Assert.Null(OperationRecovery.Interrupt(new JsonObject { ["status"] = status, ["started_at"] = 1 }, 2));
	}

	[Fact]
	public void Запись_без_начала_получает_ts_момента_восстановления()
	{
		Assert.Equal(9, OperationRecovery.Interrupt(new JsonObject { ["id"] = "x" }, 9)!["ts"]!.GetValue<long>());
	}

	[Theory]
	[InlineData("")]
	[InlineData("../x.tar.gz")]
	[InlineData("a/b.tar.gz")]
	public void Имя_снимка_только_имя_файла(string name)
	{
		Assert.False(SnapshotName.Create(name).IsSuccess);
	}
}
