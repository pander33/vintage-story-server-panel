using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Provisioning;

/// <summary>
/// Всё, чем создание и удаление сервера отличаются на Linux и Windows.
/// </summary>
/// <remarks>
/// Общий порядок шагов (проверка, каталоги, распаковка, <c>--genconfig</c>, запись в реестр)
/// находится в <see cref="ProvisionBackend"/>. Здесь решается, как игра получает свои файлы,
/// от чьего имени запускается и что значит «поставить службу» без systemd. Устроено так же,
/// как <see cref="Runtime.IServiceBackend"/>.
/// </remarks>
public interface IProvisioningPlatform
{
	/// <summary>Раскладка путей, которую выводит <see cref="InstanceLayout"/>.</summary>
	PlatformLayout Layout { get; }

	Task<RuntimeProbePayload> ProbeRuntimeAsync(CancellationToken ct);

	/// <summary>
	/// Проверить всё, без чего <c>--genconfig</c> не запустится, до первой записи на диск.
	/// </summary>
	/// <returns>Путь к dotnet.</returns>
	Task<Result<string>> PrepareAsync(InstanceLayout paths, CancellationToken ct);

	/// <summary>
	/// Подготовить каталог серверов: предки не позволяют обычному пользователю подменить его,
	/// сам каталог существует и принадлежит привилегированной стороне. Чужой непустой каталог — отказ.
	/// </summary>
	/// <param name="owned">Каталог уже принадлежит панели: текущий или прежний каталог серверов.</param>
	Task<Result<Done>> SecureServersRootAsync(ServersRoot root, bool owned, CancellationToken ct);

	/// <summary>Отдать дерево сервера игре, включая корень.</summary>
	Task<Result<Done>> GiveToGameAsync(string root, CancellationToken ct);

	/// <summary><c>dotnet VintagestoryServer.dll --dataPath … --genconfig</c> от имени игры.</summary>
	Task<HostCommandResult> GenerateConfigAsync(string dotnet, InstanceLayout paths, TimeSpan timeout, CancellationToken ct);

	/// <summary>Сделать созданный сервер запускаемым. Сверка путей уже выполнена.</summary>
	Task<Result<ServiceInstalledPayload>> InstallServiceAsync(InstanceLayout paths, RegisteredServer server, CancellationToken ct);

	/// <summary>Остановить сервер и снять его службу. Файлы сервера не трогаются.</summary>
	Task<Result<Done>> RemoveServiceAsync(InstanceLayout paths, RegisteredServer server, CancellationToken ct);

	Task<Result<PanelLogsPayload>> PanelLogsAsync(int count, CancellationToken ct);

	Task<Result<PanelRestartPayload>> RestartPanelAsync(CancellationToken ct);

	/// <summary>
	/// Правило брандмауэра для порта панели после смены адреса или порта: прежнее снимается,
	/// новое ставится, если панель видна снаружи (<see cref="PanelListener.OpensFirewall"/>).
	/// </summary>
	/// <returns>Текст для оператора, если правило не удалось поставить; иначе <see langword="null"/>.</returns>
	Task<string?> SyncPanelFirewallAsync(PanelListener previous, PanelListener current, CancellationToken ct);
}
