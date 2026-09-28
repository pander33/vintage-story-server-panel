using System.Text.Json.Nodes;
using Panel.Maintenance.Domain;
using Panel.Maintenance.Infrastructure;
using Panel.SharedKernel;

namespace Panel.Maintenance.Tests;

/// <summary>
/// Запись создания сервера в общем агрегате <see cref="Operation"/> (INV-29).
/// </summary>
public sealed class ProvisionOperationTests : IDisposable
{
	private static readonly InstanceId creative = InstanceId.Parse("creative");

	private readonly string directory = Path.Combine(Path.GetTempPath(), "vs-provision-" + Guid.NewGuid().ToString("n"));

	public void Dispose()
	{
		if (Directory.Exists(directory))
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	private static Operation Created()
	{
		var operation = Operation.CreateProvision(OperationId.New(), creative, "1.22.7", 100);
		operation.Advance(new OperationStep(StepAction.Catalogue, StepResult.Ok) { Version = "1.22.7", Size = 600 });
		operation.Advance(new OperationStep(StepAction.Download, StepResult.Ok) { Bytes = 590 });
		operation.Advance(new OperationStep(StepAction.Create, StepResult.Ok) { Root = "/srv/vs-panel/creative", TelemetryMod = "vspaneltelemetry_0.4.0.zip" });
		operation.Advance(new OperationStep(StepAction.Service, StepResult.Ok) { Unit = "vs-creative.service" });
		operation.Advance(new OperationStep(StepAction.Start, StepResult.Ok));
		return operation;
	}

	[Fact]
	public void Запись_создания_в_замороженной_форме()
	{
		var operation = Created();
		operation.NoteHealth(new JsonObject { ["state"] = "ready", ["detail"] = "Сервер отвечает" });
		operation.Advance(new OperationStep(StepAction.Ready, StepResult.Ok) { Detail = Phrase.From("Сервер отвечает") });
		operation.Succeed(200);

		var json = operation.ToJson();

		Assert.Equal(
			["id", "type", "instance", "title", "target_version", "started_at", "status", "steps", "phase", "root", "health", "detail", "finished_at", "ts"],
			json.Select(pair => pair.Key));
		Assert.Equal("instance", (string?) json["type"]);
		Assert.Equal("Создание сервера creative", json["title"].Said());
		Assert.Equal("ready", (string?) json["phase"]);
		Assert.Equal("/srv/vs-panel/creative", (string?) json["root"]);
		Assert.Equal("Сервер создан, запущен и отвечает", json["detail"].Said());

		var steps = json["steps"]!.AsArray();
		Assert.Equal(
			["catalogue", "download", "create", "service", "start", "ready"],
			steps.Select(step => (string?) step!["action"]));
		Assert.Equal(
			"""{"action":"download","status":"ok","bytes":590,"md5":"verified"}""",
			steps[1]!.ToJsonString());
		Assert.Equal(
			"""{"action":"catalogue","status":"ok","version":"1.22.7","size":600}""",
			steps[0]!.ToJsonString());
		Assert.Equal("vs-creative.service", (string?) steps[3]!["unit"]);
	}

	[Fact]
	public void Созданный_без_подтверждённой_готовности_сервер_всё_равно_создан()
	{
		var operation = Created();
		operation.NoteHealth(new JsonObject { ["state"] = "degraded", ["detail"] = "Телеметрия недоступна; автоперезапуск запрещён" });
		operation.Advance(new OperationStep(StepAction.Ready, StepResult.Warning) { Detail = Phrase.From("Телеметрия недоступна; автоперезапуск запрещён") });
		operation.Succeed(200);

		Assert.Equal(OperationStatus.Success, operation.Status);
		Assert.Equal(
			"Сервер создан и запущен, но готовность подтвердить не удалось: Телеметрия недоступна; автоперезапуск запрещён",
			operation.Detail?.ToString());
		Assert.Equal("warning", (string?) operation.ToJson()["steps"]!.AsArray()[^1]!["status"]);
	}

	[Fact]
	public void Откат_после_регистрации_пропускается_и_это_видно()
	{
		var operation = Created();
		operation.Advance(new OperationStep(StepAction.Rollback, StepResult.Skipped)
		{
			Detail = Phrase.From("Сервер уже зарегистрирован; удалите его вручную, если он не нужен"),
		});
		operation.Fail(Failure.Unavailable("systemctl start вернул 1"), 200);

		var json = operation.ToJson();
		Assert.Equal("failed", (string?) json["status"]);
		Assert.Equal("rollback", (string?) json["phase"]);
		Assert.Equal("skipped", (string?) json["steps"]!.AsArray()[^1]!["status"]);
	}

	[Fact]
	public void Не_начавшееся_создание_называется_по_своему()
	{
		var preflight = Operation.Start(OperationId.New(), OperationKind.ProvisionPreflight, 100);
		preflight.Fail(Failure.Unavailable("нет"), 101);

		Assert.Equal("preflight", (string?) preflight.ToJson()["type"]);
		Assert.Equal("Сервер не создан", preflight.Title.ToString());
		Assert.Throws<ArgumentException>(() => Operation.Start(OperationId.New(), OperationKind.Provision, 1));
	}

	[Fact]
	public async Task Прерванное_создание_уходит_в_историю_со_своим_текстом()
	{
		var store = new OperationStore(
			Path.Combine(directory, "provision.json"),
			Path.Combine(directory, "provision-runs.jsonl"),
			OperationFamily.Provision);
		await store.SaveCurrentAsync(Created(), CancellationToken.None);

		var recovered = await store.RecoverAsync(500, CancellationToken.None);

		Assert.NotNull(recovered);
		Assert.Equal("interrupted", (string?) recovered!["status"]);
		Assert.Equal(OperationRecovery.InterruptedProvisionDetail, PhraseJsonConverter.FromNode(recovered["detail"]));
		Assert.Equal(100, (long) recovered["ts"]!);
		Assert.Null(await store.ReadCurrentAsync(CancellationToken.None));
		Assert.Single(await store.RecentAsync(20, CancellationToken.None));
		Assert.True(File.Exists(Path.Combine(directory, "provision-runs.jsonl")));
		Assert.False(File.Exists(Path.Combine(directory, "update-runs.jsonl")));
	}

	[Fact]
	public void Аренда_создания_читается_обратно()
	{
		Assert.Equal("provision", LeasePurposeWire.ToWire(LeasePurpose.Provision));
		Assert.Equal(LeasePurpose.Provision, LeasePurposeWire.FromWire("provision"));
	}
}
