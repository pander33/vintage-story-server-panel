using Panel.Maintenance.Domain;
using Panel.SharedKernel;
using Panel.Web.Application;

namespace Panel.Web.Tests;

/// <summary>
/// Чтение текущей аренды для строки операций (интерфейс 2.1 §1), без файла на диске.
/// </summary>
public sealed class MaintenanceCoordinatorTests
{
	private static readonly InstanceId main = InstanceId.Create("main").Value;
	private static readonly InstanceId other = InstanceId.Create("other").Value;

	[Fact]
	public void Current_равен_null_пока_сервер_свободен()
	{
		var coordinator = new MaintenanceCoordinator();

		Assert.Null(coordinator.Current(main));
	}

	[Fact]
	public void Current_отдаёт_аренду_занятого_сервера()
	{
		var coordinator = new MaintenanceCoordinator();
		using var lease = coordinator.TryAcquire(main, LeasePurpose.Update)!;

		var current = coordinator.Current(main);

		Assert.NotNull(current);
		Assert.Equal(LeasePurpose.Update, current!.Purpose);
		Assert.Equal(main, current.Instance);
	}

	[Fact]
	public void Current_не_видит_аренду_другого_сервера()
	{
		var coordinator = new MaintenanceCoordinator();
		using var lease = coordinator.TryAcquire(other, LeasePurpose.ScheduledMaintenance)!;

		Assert.Null(coordinator.Current(main));
	}

	[Fact]
	public void Current_не_захватывает_и_не_освобождает_аренду()
	{
		var coordinator = new MaintenanceCoordinator();
		using var lease = coordinator.TryAcquire(main, LeasePurpose.Provision)!;

		coordinator.Current(main);
		coordinator.Current(main);

		Assert.True(coordinator.IsBusy(main));
		Assert.Null(coordinator.TryAcquire(main, LeasePurpose.Provision));
	}
}
