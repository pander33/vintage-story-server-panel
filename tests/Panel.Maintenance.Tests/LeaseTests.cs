using Panel.Maintenance.Domain;
using Panel.Maintenance.Infrastructure;
using Panel.SharedKernel;

namespace Panel.Maintenance.Tests;

/// <summary>Аренда обслуживания на диске (детализация Maintenance §4 и §6).</summary>
public sealed class LeaseTests : IDisposable
{
	private static readonly InstanceId main = InstanceId.Parse("main");
	private static readonly LeaseHolder me = new("boot", 100, 5000);
	private static readonly LeaseHolder other = new("boot", 200, 7000);

	private readonly string directory = Path.Combine(Path.GetTempPath(), "vs-lease-" + Guid.NewGuid().ToString("n"));

	private string LeaseFile => Path.Combine(directory, "maintenance-lease.json");

	public void Dispose()
	{
		if (Directory.Exists(directory))
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void Брошенная_только_если_владелец_не_этот_и_мёртв()
	{
		var lease = MaintenanceLease.Acquire(LeasePurpose.Update, main, other, 1);

		Assert.True(lease.IsAbandoned(me, holderAlive: false));
		Assert.False(lease.IsAbandoned(me, holderAlive: true));
		Assert.False(MaintenanceLease.Acquire(LeasePurpose.Update, main, me, 1).IsAbandoned(me, holderAlive: false));
	}

	[Fact]
	public void Тот_же_pid_после_перезапуска_не_тот_же_владелец()
	{
		Assert.NotEqual(new LeaseHolder("boot", 100, 5000), new LeaseHolder("boot", 100, 5001));
		Assert.NotEqual(new LeaseHolder("boot-a", 100, 5000), new LeaseHolder("boot-b", 100, 5000));
	}

	[Fact]
	public void Файл_аренды_читается_обратно()
	{
		var lease = MaintenanceLease.Acquire(LeasePurpose.WatchdogRecovery, main, me, 42);

		var read = MaintenanceLease.FromJson(lease.ToJson());

		Assert.Equal(lease, read);
		Assert.Equal("watchdog-recovery", lease.ToJson()["purpose"]!.GetValue<string>());
	}

	[Fact]
	public void Второй_захват_отказан_пока_первый_жив()
	{
		var store = new FileLeaseStore(LeaseFile);
		var first = MaintenanceLease.Acquire(LeasePurpose.Update, main, other, 1);
		var second = MaintenanceLease.Acquire(LeasePurpose.ScheduledMaintenance, main, me, 2);

		Assert.True(store.TryAcquire(first, _ => false));
		Assert.False(store.TryAcquire(second, held => held.IsAbandoned(me, holderAlive: true)));
		Assert.Equal(first.Id, store.Read().Lease!.Id);
	}

	[Fact]
	public void Аренда_мёртвого_владельца_перехватывается()
	{
		var store = new FileLeaseStore(LeaseFile);
		store.TryAcquire(MaintenanceLease.Acquire(LeasePurpose.Update, main, other, 1), _ => false);
		var mine = MaintenanceLease.Acquire(LeasePurpose.Update, main, me, 2);

		Assert.True(store.TryAcquire(mine, held => held.IsAbandoned(me, holderAlive: false)));
		Assert.Equal(mine.Id, store.Read().Lease!.Id);
	}

	[Fact]
	public void Нечитаемый_файл_не_держит_панель()
	{
		Directory.CreateDirectory(directory);
		File.WriteAllText(LeaseFile, "{broken");
		var store = new FileLeaseStore(LeaseFile);

		Assert.True(store.Read() is { Exists: true, Lease: null });
		Assert.True(store.TryAcquire(MaintenanceLease.Acquire(LeasePurpose.Update, main, me, 1), _ => false));
	}

	[Fact]
	public void Отпускается_только_своя_аренда()
	{
		var store = new FileLeaseStore(LeaseFile);
		var held = MaintenanceLease.Acquire(LeasePurpose.Update, main, me, 1);
		store.TryAcquire(held, _ => false);

		store.Release(Guid.NewGuid());
		Assert.True(store.Read().Exists);

		store.Release(held.Id);
		Assert.False(store.Read().Exists);
	}

	[Fact]
	public void Текущий_процесс_жив_по_своей_пробе()
	{
		var probe = new ProcessProbe();

		Assert.True(probe.IsAlive(probe.Current()));
		Assert.False(probe.IsAlive(probe.Current() with { StartTicks = probe.Current().StartTicks + 1, Boot = "другая" }));
	}
}
