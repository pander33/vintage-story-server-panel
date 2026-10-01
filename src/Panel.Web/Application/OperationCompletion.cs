using Panel.Maintenance.Domain;
using Panel.Maintenance.Infrastructure;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>Завершение операции сохраняется в истории до удаления текущей записи.</summary>
internal static class OperationCompletion
{
	internal static Task ArchiveAsync(OperationStore store, Operation operation, Failure? reason, long now)
	{
		if (reason is null)
		{
			operation.Succeed(now);
		}
		else
		{
			operation.Fail(reason, now);
		}

		return store.ArchiveAsync(operation, CancellationToken.None);
	}
}
