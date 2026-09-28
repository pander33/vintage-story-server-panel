using System.Text.Json.Nodes;
using Panel.Agent.Content;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Tests;

public sealed class OperationTrackerTests : IDisposable
{
	private readonly string directory = Path.Combine("operation-tracker-tests", Guid.NewGuid().ToString("n"));
	private readonly OperationTracker tracker = new(TimeProvider.System);

	public OperationTrackerTests() => Directory.CreateDirectory(directory);

	public void Dispose() => Directory.Delete(directory, recursive: true);

	[Fact]
	public void Nothing_is_active_before_an_operation_begins()
	{
		var snapshot = tracker.Snapshot("example");

		Assert.False(snapshot.Active);
		Assert.Empty(snapshot.Stages);
	}

	[Fact]
	public void Snapshot_reports_stages_and_current_stage()
	{
		using var scope = tracker.Begin("example", "backup", OperationStages.Backup, OperationStages.BackupTitle);
		scope.Enter(OperationStages.Pack);

		var snapshot = tracker.Snapshot("example");

		Assert.True(snapshot.Active);
		Assert.Equal("backup", snapshot.Kind);
		Assert.Equal(OperationStages.Backup, snapshot.Stages);
		Assert.Equal(OperationStages.Backup.ToList().IndexOf(OperationStages.Pack), snapshot.Stage);
		Assert.False(tracker.Snapshot("other").Active);
	}

	[Fact]
	public void Stage_missing_from_the_operation_is_ignored()
	{
		using var scope = tracker.Begin("example", "restore-world", OperationStages.RestoreWorld, OperationStages.RestoreWorldTitle);
		scope.Enter(OperationStages.Extracting);

		scope.Enter(OperationStages.Pack);

		Assert.Equal(1, tracker.Snapshot("example").Stage);
	}

	[Fact]
	public void Bytes_are_counted_within_a_stage_and_reset_on_the_next()
	{
		using var scope = tracker.Begin("example", "backup", OperationStages.Backup, OperationStages.BackupTitle);
		scope.Enter(OperationStages.Pack);
		scope.Advance(50);
		Assert.Null(tracker.Snapshot("example").DoneBytes);

		scope.Measure(200);
		scope.Advance(50);
		scope.Advance(500);
		var counting = tracker.Snapshot("example");
		Assert.Equal(200, counting.DoneBytes);
		Assert.Equal(200, counting.TotalBytes);

		scope.Enter(OperationStages.Check);
		var next = tracker.Snapshot("example");
		Assert.Null(next.DoneBytes);
		Assert.Null(next.TotalBytes);
	}

	[Fact]
	public void Disposing_ends_the_operation_but_never_a_newer_one()
	{
		var first = tracker.Begin("example", "backup", OperationStages.Backup, OperationStages.BackupTitle);
		using var second = tracker.Begin("example", "export", OperationStages.Export, OperationStages.ExportTitle);

		first.Dispose();
		Assert.Equal("export", tracker.Snapshot("example").Kind);

		second.Dispose();
		Assert.False(tracker.Snapshot("example").Active);
	}

	[Fact]
	public async Task Export_moves_through_stages_up_to_verification()
	{
		var server = CreateServer();
		File.WriteAllText(Path.Combine(server.WorldDirectory, "active.vcdbs"), "world");
		using var scope = tracker.Begin("example", "export", OperationStages.Export, OperationStages.ExportTitle);

		var result = await new BackupBackend(TimeProvider.System)
			.ExportAsync(server, Path.Combine(directory, "transfer.tar.gz"), CancellationToken.None, scope);

		Assert.True(result.IsSuccess, result.Failure?.Message);
		var snapshot = tracker.Snapshot("example");
		Assert.Equal(OperationStages.Check, snapshot.Stages[snapshot.Stage]);
	}

	[Fact]
	public async Task Progress_stream_counts_every_byte_read()
	{
		var path = Path.Combine(directory, "data.bin");
		File.WriteAllBytes(path, new byte[10_000]);
		using var scope = tracker.Begin("example", "backup", OperationStages.Backup, OperationStages.BackupTitle);
		scope.Measure(10_000);

		await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read))
		await using (var counted = new ProgressStream(file, scope))
		{
			await counted.CopyToAsync(Stream.Null);
		}

		Assert.Equal(10_000, tracker.Snapshot("example").DoneBytes);
	}

	private RegisteredServer CreateServer()
	{
		var data = Path.Combine(directory, "data");
		var saves = Path.Combine(data, "Saves");
		var mods = Path.Combine(data, "Mods");
		Directory.CreateDirectory(saves);
		Directory.CreateDirectory(mods);
		File.WriteAllText(Path.Combine(data, "serverconfig.json"), new JsonObject
		{
			["WorldConfig"] = new JsonObject { ["SaveFileLocation"] = Path.Combine(saves, "active.vcdbs") },
		}.ToJsonString());
		return new RegisteredServer
		{
			Id = InstanceId.Parse("example"),
			Name = "Example",
			Unit = "vs-example",
			GameVersion = "1.22.7",
			DataDirectory = data,
			WorldDirectory = saves,
			ModsDirectory = mods,
			ConfigFile = Path.Combine(data, "serverconfig.json"),
		};
	}
}
