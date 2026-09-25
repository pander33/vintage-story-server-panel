using Panel.Maintenance.Domain;
using Panel.Maintenance.Infrastructure;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Одна долгая операция на всю панель: плановое обслуживание, восстановление наблюдателем,
/// обновления модов и игры, создание серверов.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TryAcquire"/> синхронно проверяет занятость и возвращает владение, которое затем
/// передаётся задаче, поэтому между проверкой и созданием задачи нет окна.
/// </para>
/// <para>
/// Сервер занят, пока на него есть аренда; аренда освобождается в <c>Dispose</c>. Она
/// записывается на диск (<see cref="FileLeaseStore"/>, детализация Maintenance §4): запись
/// файла и есть захват, и после аварии панели при старте видно, чья аренда осталась.
/// </para>
/// </remarks>
public sealed class MaintenanceCoordinator
{
	private readonly object sync = new();
	private readonly FileLeaseStore? store;
	private readonly ProcessProbe probe;
	private readonly TimeProvider clock;
	private Lease? held;

	/// <summary>Координатор без файла, для тестов: аренда хранится только в памяти.</summary>
	public MaintenanceCoordinator()
		: this(null, new ProcessProbe(), TimeProvider.System)
	{
	}

	public MaintenanceCoordinator(FileLeaseStore? store, ProcessProbe probe, TimeProvider clock)
	{
		ArgumentNullException.ThrowIfNull(probe);
		ArgumentNullException.ThrowIfNull(clock);
		this.store = store;
		this.probe = probe;
		this.clock = clock;
	}

	/// <summary>Занят ли сервер; изменяющие маршруты в этом случае отвечают 409.</summary>
	public bool IsBusy(InstanceId instance)
	{
		lock (sync)
		{
			return held is { } lease && lease.Instance == instance;
		}
	}

	public bool IsLocked
	{
		get
		{
			lock (sync)
			{
				return held is not null;
			}
		}
	}

	/// <summary>
	/// Аренда или <c>null</c>, если панель уже занята другой операцией этого или другого живого
	/// процесса. Если файл не удалось записать, это тоже отказ: без записи на диске после аварии
	/// не было бы видно, что операция шла.
	/// </summary>
	public Lease? TryAcquire(InstanceId instance, LeasePurpose purpose)
	{
		lock (sync)
		{
			if (held is not null)
			{
				return null;
			}

			var current = probe.Current();
			var record = MaintenanceLease.Acquire(purpose, instance, current, clock.GetUtcNow().ToUnixTimeSeconds());
			if (store is not null)
			{
				try
				{
					if (!store.TryAcquire(record, held => held.IsAbandoned(current, probe.IsAlive(held.Holder))))
					{
						return null;
					}
				}
				catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
				{
					return null;
				}
			}

			held = new Lease(this, record);
			return held;
		}
	}

	private void Release(Lease lease)
	{
		lock (sync)
		{
			if (!ReferenceEquals(held, lease))
			{
				return;
			}

			held = null;
			try
			{
				store?.Release(lease.Record.Id);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// Оставшийся файл принадлежит живому процессу и станет брошенным после его остановки; в
				// памяти аренда уже отпущена.
			}
		}
	}

	public sealed class Lease : IDisposable
	{
		private readonly MaintenanceCoordinator owner;

		internal Lease(MaintenanceCoordinator owner, MaintenanceLease record)
		{
			this.owner = owner;
			Record = record;
		}

		public MaintenanceLease Record { get; }

		public InstanceId Instance => Record.Instance;

		public LeasePurpose Purpose => Record.Purpose;

		public void Dispose() => owner.Release(this);
	}
}
