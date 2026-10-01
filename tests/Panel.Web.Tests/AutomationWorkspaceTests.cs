using System.Reflection;
using System.Text.Json.Nodes;
using Panel.Web.Application;

namespace Panel.Web.Tests;

public sealed class AutomationWorkspaceTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "vs-automation-state-" + Guid.NewGuid().ToString("N"));

	[Fact]
	public async Task Ожидающая_запись_делает_снимок_после_захвата_блокировки()
	{
		var state = Path.Combine(directory, "automation.json");
		var workspace = await AutomationWorkspace.OpenAsync(state, Path.Combine(directory, "runs.jsonl"), TimeProvider.System, TimeZoneInfo.Utc);
		var gate = (SemaphoreSlim) typeof(AutomationWorkspace).GetField("saving", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(workspace)!;
		await gate.WaitAsync();
		Task save;
		try
		{
			lock (workspace.Sync) workspace.Health["failures"] = 1;
			save = workspace.SaveAsync(default);
			Assert.False(save.IsCompleted);
			lock (workspace.Sync) workspace.Health["failures"] = 2;
		}
		finally
		{
			gate.Release();
		}
		await save;
		var stored = JsonNode.Parse(await File.ReadAllTextAsync(state))!;
		Assert.Equal(2, stored["health"]!["failures"]!.GetValue<int>());
	}

	[Fact]
	public async Task Отмена_ожидания_не_ломает_следующую_запись()
	{
		var state = Path.Combine(directory, "automation.json");
		var workspace = await AutomationWorkspace.OpenAsync(state, Path.Combine(directory, "runs.jsonl"), TimeProvider.System, TimeZoneInfo.Utc);
		using var canceled = new CancellationTokenSource();
		canceled.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.SaveAsync(canceled.Token));
		await workspace.SaveAsync(default);
		Assert.True(File.Exists(state));
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task Отказ_записи_возвращается_и_не_блокирует_следующую_попытку(bool blockTarget)
	{
		Directory.CreateDirectory(directory);
		var obstruction = Path.Combine(directory, "blocked");
		var state = blockTarget ? obstruction : Path.Combine(obstruction, "automation.json");
		if (blockTarget) Directory.CreateDirectory(obstruction);
		else await File.WriteAllTextAsync(obstruction, "file");
		var workspace = await AutomationWorkspace.OpenAsync(state, Path.Combine(directory, "runs.jsonl"), TimeProvider.System, TimeZoneInfo.Utc);
		var failed = await workspace.SaveAsync(default);
		Assert.False(failed.IsSuccess);
		Assert.Equal(Panel.SharedKernel.FailureCode.Unavailable, failed.Failure!.Code);
		await Assert.ThrowsAsync<IOException>(() => workspace.SaveRequiredAsync(default));

		if (blockTarget) Directory.Delete(obstruction);
		else File.Delete(obstruction);
		Assert.True((await workspace.SaveAsync(default)).IsSuccess);
		Assert.True(File.Exists(state));
	}

	public void Dispose()
	{
		if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
	}
}
