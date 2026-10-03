using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Agent.Content;
using Panel.Content.Domain;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.Fleet.Infrastructure;
using Panel.Fleet.Ports;
using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Provisioning;

/// <summary>
/// Создание сервера и дела самой панели на привилегированной стороне: <c>instance-create</c>,
/// <c>instance-service-install</c>, <c>runtime-probe</c>, <c>disk-free</c>,
/// <c>panel-env-set</c>, <c>panel-restart</c>.
/// </summary>
/// <remarks>
/// <para>
/// Создание — одна привилегированная команда, все пути выбирает демон: панель
/// присылает описание сервера с закрытым набором ключей и имя пакета в staging. Сбой во время
/// создания удаляет созданное дерево; после записи в реестр ничего не удаляется.
/// </para>
/// <para>
/// Порядок шагов общий для обеих платформ. Как игра получает файлы, от чьего имени
/// запускается <c>--genconfig</c> и что значит «поставить службу», решает
/// <see cref="IProvisioningPlatform"/>: <see cref="SystemdProvisioning"/> на Linux,
/// <see cref="SupervisedProvisioning"/> на Windows.
/// </para>
/// </remarks>
public sealed class ProvisionBackend
{
	private readonly InstanceProvisioner instances;
	private readonly InstanceTransfer transfer;
	private readonly InstanceLifecycle lifecycle;
	private readonly PanelEnvironmentBackend panel;

	public ProvisionBackend(
		string panelConfigPath,
		IRegistryStore registry,
		RegistryEditor editor,
		StagingArea staging,
		IProvisioningPlatform platform,
		GamePorts ports)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(panelConfigPath);
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(editor);
		ArgumentNullException.ThrowIfNull(staging);
		ArgumentNullException.ThrowIfNull(platform);
		ArgumentNullException.ThrowIfNull(ports);
		var settings = new ProvisioningConfiguration(panelConfigPath, platform);
		instances = new InstanceProvisioner(settings, registry, editor, staging, platform);
		transfer = new InstanceTransfer(settings, registry, editor, staging, platform);
		lifecycle = new InstanceLifecycle(settings, registry, editor, platform, ports);
		panel = new PanelEnvironmentBackend(settings, platform);
	}

	public Task<Result<TransferInspectionPayload>> InspectTransferAsync(string name, string sha256, string length, CancellationToken ct) =>
		transfer.InspectTransferAsync(name, sha256, length, ct);

	public Task<Result<InstanceCreatedPayload>> ImportAsync(
		JsonNode? specification, string transferName, string sha256, string length, CancellationToken ct) =>
		transfer.ImportAsync(specification, transferName, sha256, length, ct);

	public Task<Result<InstanceCreatedPayload>> CreateAsync(JsonNode? specification, CancellationToken ct) =>
		instances.CreateAsync(specification, ct);

	public Task<Result<ServiceInstalledPayload>> InstallServiceAsync(InstanceId instance, CancellationToken ct) =>
		lifecycle.InstallServiceAsync(instance, ct);

	public Task<Result<InstanceDeletedPayload>> DeleteInstanceAsync(InstanceId instance, CancellationToken ct) =>
		lifecycle.DeleteInstanceAsync(instance, ct);

	public Task<RuntimeProbePayload> ProbeRuntimeAsync(CancellationToken ct) =>
		panel.ProbeRuntimeAsync(ct);

	public Task<Result<DiskFreePayload>> DiskFreeAsync(CancellationToken ct) =>
		panel.DiskFreeAsync(ct);

	public Task<Result<PanelLogsPayload>> PanelLogsAsync(string lines, CancellationToken ct) =>
		panel.PanelLogsAsync(lines, ct);

	public Task<Result<PanelEnvironmentSetPayload>> SetPanelEnvironmentAsync(PanelSetting setting, CancellationToken ct) =>
		panel.SetPanelEnvironmentAsync(setting, ct);

	public Task<Result<InstancesRootSetPayload>> SetInstancesRootAsync(string raw, CancellationToken ct) =>
		panel.SetInstancesRootAsync(raw, ct);

	public Task<Result<PanelRestartPayload>> RestartPanelAsync(CancellationToken ct) =>
		panel.RestartPanelAsync(ct);

	public static Result<string> RewriteInstancesRoot(string text, ServersRoot chosen, string previous) =>
		PanelEnvironmentBackend.RewriteInstancesRoot(text, chosen, previous);

	internal static Dictionary<string, string> EnvironmentValues(IEnumerable<string> lines) =>
		PanelEnvironmentBackend.EnvironmentValues(lines);

	internal static Task<Result<Done>> WriteAtomicallyAsync(string path, string content, UnixFileMode mode, CancellationToken ct) =>
		ProvisioningFiles.WriteAtomicallyAsync(path, content, mode, ct);

	internal static void TryDelete(string directory) => ProvisioningFiles.TryDelete(directory);
}
