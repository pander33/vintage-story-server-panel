using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.Maintenance.Infrastructure;

namespace Panel.Web.Application;

/// <summary>
/// Разбор того, что осталось от прошлого запуска панели: брошенная аренда и операции в
/// <c>current</c>. Детализация Maintenance §7.
/// </summary>
/// <remarks>
/// <para>
/// Сервис запускается раньше такта автоматизации: он зарегистрирован в <c>Program.cs</c>
/// раньше, а службы хоста стартуют по порядку.
/// </para>
/// <para>
/// Записи разбираются сразу для всего реестра, иначе прерванная операция попала бы в историю
/// только при открытии вкладки. Сервер, добавленный позже, разбирать не нужно.
/// </para>
/// <para>
/// Отличие от §7: события <c>OperationInterrupted</c> на шине нет. Добавлять уведомление в
/// Discord или Telegram о прерванной операции должен решать владелец.
/// </para>
/// </remarks>
public sealed partial class OperationRecoveryService : IHostedService
{
	private readonly FileLeaseStore leases;
	private readonly ProcessProbe probe;
	private readonly IRegistryStore registry;
	private readonly UpdateService updates;
	private readonly ProvisionService provision;
	private readonly ILogger<OperationRecoveryService> log;

	public OperationRecoveryService(
		FileLeaseStore leases,
		ProcessProbe probe,
		IRegistryStore registry,
		UpdateService updates,
		ProvisionService provision,
		ILogger<OperationRecoveryService> log)
	{
		this.leases = leases;
		this.probe = probe;
		this.registry = registry;
		this.updates = updates;
		this.provision = provision;
		this.log = log;
	}

	public async Task StartAsync(CancellationToken cancellationToken)
	{
		ReleaseAbandonedLease();

		// Создание сервера пишет запись на уровне панели: сервер, в каталоге которого её можно было
		// бы хранить, мог так и не появиться.
		try
		{
			await provision.RecoverAsync().ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			RecoveryFailed(log, "provision", exception);
		}

		var snapshot = await registry.LoadAsync(cancellationToken).ConfigureAwait(false);
		if (snapshot is not RegistrySnapshot.Available available)
		{
			return;
		}

		foreach (var server in available.Servers)
		{
			try
			{
				await updates.OpenAsync(server.Id).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				RecoveryFailed(log, server.Id.Value, exception);
			}
		}
	}

	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	private void ReleaseAbandonedLease()
	{
		try
		{
			var record = leases.Read();
			if (!record.Exists)
			{
				return;
			}

			if (record.Lease is { } lease)
			{
				if (!lease.IsAbandoned(probe.Current(), probe.IsAlive(lease.Holder)))
				{
					// Аренду держит другой живой процесс. Панель её не трогает, и захват будет отклоняться,
					// пока тот процесс жив.
					LeaseHeld(log, lease.Holder.Serialize(), lease.Instance.Value);
					return;
				}

				LeaseAbandoned(log, lease.Instance.Value, lease.Holder.Serialize());
			}

			leases.Delete();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			LeaseUnreadable(log, leases.Path, exception);
		}
	}

	[LoggerMessage(Level = LogLevel.Warning, Message = "Брошенная аренда обслуживания сервера {Instance} (процесс {Holder}) снята")]
	private static partial void LeaseAbandoned(ILogger logger, string instance, string holder);

	[LoggerMessage(Level = LogLevel.Warning, Message = "Аренду обслуживания держит живой процесс {Holder} (сервер {Instance})")]
	private static partial void LeaseHeld(ILogger logger, string holder, string instance);

	[LoggerMessage(Level = LogLevel.Error, Message = "Файл аренды {Path} недоступен")]
	private static partial void LeaseUnreadable(ILogger logger, string path, Exception exception);

	[LoggerMessage(Level = LogLevel.Error, Message = "Не удалось разобрать операции сервера {Instance}")]
	private static partial void RecoveryFailed(ILogger logger, string instance, Exception exception);
}
