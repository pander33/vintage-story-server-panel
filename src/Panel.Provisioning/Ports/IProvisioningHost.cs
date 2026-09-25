using Panel.Contracts.Agent;
using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Provisioning.Ports;

/// <summary>
/// Привилегированная сторона с точки зрения создания сервера и мастера установки.
/// </summary>
/// <remarks>
/// Все вызовы адресованы реестру («-»): нужного сервера ещё нет, а окружение, перезапуск и
/// журнал относятся к самой панели. Узкий порт по ADR-01: восемь действий вместо 62.
/// </remarks>
public interface IProvisioningHost
{
	Task<Result<RuntimeProbePayload>> RuntimeProbeAsync(CancellationToken ct);

	Task<Result<DiskFreePayload>> DiskFreeAsync(CancellationToken ct);

	/// <summary>Создать дерево и записать сервер в реестр. Пакет уже лежит в staging.</summary>
	Task<Result<InstanceCreatedPayload>> CreateInstanceAsync(ServerSpec spec, StagedFile package, CancellationToken ct);

	Task<Result<ServiceInstalledPayload>> InstallServiceAsync(InstanceId instance, CancellationToken ct);

	Task<Result<PanelEnvironmentSetPayload>> SetPanelEnvironmentAsync(PanelSetting setting, CancellationToken ct);

	Task<Result<PanelRestartPayload>> RestartPanelAsync(CancellationToken ct);

	/// <summary>
	/// Каталог новых серверов. Путь проверяет демон, панель только передаёт выбор оператора.
	/// </summary>
	Task<Result<InstancesRootSetPayload>> SetInstancesRootAsync(string path, CancellationToken ct);

	/// <summary>Журнал юнита панели: от 20 до 2000 последних строк; предел проверяет и демон.</summary>
	Task<Result<PanelLogsPayload>> PanelLogsAsync(int lines, CancellationToken ct);
}
