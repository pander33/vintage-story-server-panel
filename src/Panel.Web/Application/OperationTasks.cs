using System.Collections.Concurrent;

namespace Panel.Web.Application;

/// <summary>Учёт начатых операций для штатной остановки панели.</summary>
internal sealed class OperationTasks
{
	private readonly ConcurrentDictionary<Task, byte> running = new();

	internal void Track(Task task)
	{
		running.TryAdd(task, 0);
		_ = task.ContinueWith(done => running.TryRemove(done, out _), TaskScheduler.Default);
	}

	internal Task WaitAsync(CancellationToken ct) => Task.WhenAll(running.Keys).WaitAsync(ct);
}
