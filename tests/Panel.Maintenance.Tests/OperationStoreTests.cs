using System.Text.Json.Nodes;
using Panel.Maintenance.Domain;
using Panel.Maintenance.Infrastructure;

namespace Panel.Maintenance.Tests;

/// <summary><c>updates.json</c> и <c>update-runs.jsonl</c>: прерванная операция уходит в историю.</summary>
public sealed class OperationStoreTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "vs-ops-" + Guid.NewGuid().ToString("n"));

	public void Dispose()
	{
		if (Directory.Exists(directory))
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public async Task Прерванная_операция_уходит_в_историю()
	{
		Directory.CreateDirectory(directory);
		await File.WriteAllTextAsync(
			Path.Combine(directory, "updates.json"),
			"""{"current":{"id":"old","title":"Update","started_at":100,"steps":[]}}""");
		var store = new OperationStore(directory);

		var recovered = await store.RecoverAsync(500, default);

		Assert.Equal("interrupted", recovered!["status"]!.GetValue<string>());
		Assert.Null(await store.ReadCurrentAsync(default));
		var history = await store.RecentAsync(30, default);
		Assert.Equal("interrupted", Assert.Single(history)["status"]!.GetValue<string>());
		Assert.Equal("old", history[0]["id"]!.GetValue<string>());
	}

	[Fact]
	public async Task Завершённая_но_не_убранная_запись_не_дублируется()
	{
		var store = new OperationStore(directory);
		var operation = Operation.Start(OperationId.New(), OperationKind.Mods, 100);
		operation.Succeed(200);

		// Сбой между записью в историю и очисткой current: запись в обоих местах.
		await store.ArchiveAsync(operation, default);
		await store.SaveCurrentAsync(operation, default);

		Assert.Null(await store.RecoverAsync(300, default));
		Assert.Equal("success", Assert.Single(await store.RecentAsync(30, default))["status"]!.GetValue<string>());
		Assert.Null(await store.ReadCurrentAsync(default));
	}

	[Fact]
	public async Task Файл_состояния_в_замороженной_форме()
	{
		var store = new OperationStore(directory);
		var operation = Operation.Start(OperationId.New(), OperationKind.GameRollback, 100);

		await store.SaveCurrentAsync(operation, default);
		var root = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "updates.json")))!.AsObject();
		Assert.Equal(["version", "current"], root.Select(pair => pair.Key));
		Assert.Equal("game_rollback", root["current"]!["type"]!.GetValue<string>());

		await store.ArchiveAsync(Finished(operation), default);
		root = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "updates.json")))!.AsObject();
		Assert.Null(root["current"]);
		Assert.True(root.ContainsKey("current"));
	}

	[Fact]
	public async Task История_новые_первыми_и_битые_строки_пропускаются()
	{
		Directory.CreateDirectory(directory);
		await File.WriteAllLinesAsync(Path.Combine(directory, "update-runs.jsonl"), ["""{"id":"a"}""", "{broken", """{"id":"b"}"""]);

		var history = await new OperationStore(directory).RecentAsync(30, default);

		Assert.Equal(["b", "a"], history.Select(item => item["id"]!.GetValue<string>()));
	}

	private static Operation Finished(Operation operation)
	{
		operation.Succeed(200);
		return operation;
	}
}
