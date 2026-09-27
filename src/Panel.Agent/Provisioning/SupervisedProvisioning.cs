using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Panel.Agent.Supervision;
using Panel.Content.Domain;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Provisioning;

/// <summary>
/// Создание сервера на Windows: служба ОС не ставится, сервер запускается дочерним
/// процессом демона.
/// </summary>
/// <remarks>
/// <para>
/// Владелец файлов не меняется: доступ к дереву задаёт список доступа каталога серверов,
/// который ставит установщик в <c>ProgramData\vs-panel</c> или демон для каталога,
/// выбранного оператором (<see cref="WindowsServersRoot"/>), и он наследуется всем
/// содержимым.
/// </para>
/// <para>
/// Игра, в том числе <c>--genconfig</c>, работает от той же учётной записи, что и демон:
/// LocalSystem, от которой установщик ставит службы (findings §69). Отдельной учётной записи
/// для игры нет. INV-42 относится к Linux, где демон работает от root.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SupervisedProvisioning : IProvisioningPlatform
{
	/// <summary>Служба веб-процесса панели на Windows.</summary>
	public const string PanelService = "vs-panel";

	/// <summary>Сколько ждать остановки сервера при удалении, как в systemd-бэкенде.</summary>
	private static readonly TimeSpan removeTimeout = TimeSpan.FromSeconds(300);

	/// <summary>Пауза перед перезапуском панели, чтобы ответ успел дойти до браузера.</summary>
	private static readonly TimeSpan restartDelay = TimeSpan.FromSeconds(1);

	private static readonly TimeSpan panelStopWait = TimeSpan.FromSeconds(60);

	private readonly SupervisedBackend supervisor;

	public SupervisedProvisioning(SupervisedBackend supervisor)
	{
		ArgumentNullException.ThrowIfNull(supervisor);
		this.supervisor = supervisor;
	}

	public PlatformLayout Layout => PlatformLayout.Windows;

	public Task<Result<Done>> SecureServersRootAsync(ServersRoot root, bool owned, CancellationToken ct) =>
		Task.FromResult(WindowsServersRoot.Secure(root, owned));

	public Task<RuntimeProbePayload> ProbeRuntimeAsync(CancellationToken ct) => HostTools.ProbeRuntimeAsync(ct);

	public Task<Result<string>> PrepareAsync(InstanceLayout paths, CancellationToken ct) =>
		Task.FromResult(SupervisedBackend.DotnetPath() is { } dotnet
			? Result.Ok(dotnet)
			: Result.Fail<string>(Failure.Unavailable("dotnet not found; install the .NET runtime the game requires")));

	public Task<Result<Done>> GiveToGameAsync(string root, CancellationToken ct) => Task.FromResult<Result<Done>>(Done.Ok);

	/// <summary>
	/// Окружение наследуется от демона: без <c>SystemRoot</c> и соседних переменных dotnet на
	/// Windows не запускается, а секретов в окружении службы нет.
	/// </summary>
	public Task<HostCommandResult> GenerateConfigAsync(string dotnet, InstanceLayout paths, TimeSpan timeout, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(paths);

		return HostTools.RunAsync(
			dotnet,
			[Path.Combine(paths.ProgramDirectory, ServerPackage.ServerDll), "--dataPath", paths.DataDirectory, "--genconfig"],
			timeout,
			ct,
			workingDirectory: paths.ProgramDirectory);
	}

	public async Task<Result<ServiceInstalledPayload>> InstallServiceAsync(
		InstanceLayout paths, RegisteredServer server, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(paths);

		var adopted = await supervisor.AdoptAsync(server, ct).ConfigureAwait(false);
		return adopted.IsSuccess
			? new ServiceInstalledPayload { Id = paths.Id.Value, Unit = paths.Unit, Path = adopted.Value }
			: Result.Fail<ServiceInstalledPayload>(adopted.Failure!);
	}

	public Task<Result<Done>> RemoveServiceAsync(InstanceLayout paths, RegisteredServer server, CancellationToken ct) =>
		supervisor.ForgetAsync(server, removeTimeout, ct);

	/// <summary>
	/// Журнал служб панели из журнала событий Windows (<see cref="EventJournal"/>) в формате
	/// journalctl.
	/// </summary>
	public Task<Result<PanelLogsPayload>> PanelLogsAsync(int count, CancellationToken ct)
	{
		try
		{
			return Task.FromResult<Result<PanelLogsPayload>>(new PanelLogsPayload { Lines = EventJournal.Tail(count), Unit = PanelService });
		}
		catch (Exception exception) when (exception is EventLogException or UnauthorizedAccessException)
		{
			return Task.FromResult<Result<PanelLogsPayload>>(Failure.Unavailable($"Журнал событий Windows не читается: {exception.Message}"));
		}
	}

	/// <summary>
	/// Перезапуск службы <c>vs-panel</c>. Как <c>--no-block</c> на Linux: ответ возвращается
	/// сразу, а остановка и запуск идут после паузы, потому что остановлена будет сама панель,
	/// которая ждёт этого ответа.
	/// </summary>
	public Task<Result<PanelRestartPayload>> RestartPanelAsync(CancellationToken ct)
	{
		try
		{
			using var probe = new ServiceController(PanelService);
			_ = probe.Status;
		}
		catch (InvalidOperationException)
		{
			return Task.FromResult<Result<PanelRestartPayload>>(
				Failure.Unavailable($"Служба панели {PanelService} не установлена; перезапустите панель вручную"));
		}

		_ = Task.Run(RestartPanelLaterAsync, CancellationToken.None);
		return Task.FromResult<Result<PanelRestartPayload>>(new PanelRestartPayload { Unit = PanelService });
	}

	/// <summary>
	/// Правило брандмауэра Windows для порта панели, как у установщика (<c>PanelAccess</c>).
	/// Правило одно, по имени: прежнее снимается, каким бы ни был прежний порт.
	/// </summary>
	public async Task<string?> SyncPanelFirewallAsync(PanelListener previous, PanelListener current, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(previous);
		ArgumentNullException.ThrowIfNull(current);
		if (previous == current)
		{
			return null;
		}

		var netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
		await HostTools.RunAsync(netsh, PanelListener.NetshDelete, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
		if (current.NetshAdd is not { } rule)
		{
			return null;
		}

		var added = await HostTools.RunAsync(netsh, rule, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
		return added.ExitCode == 0 ? null : $"Порт панели {current.Port} в брандмауэре Windows не открыт: {added.Tail("netsh отказал")}";
	}

	private static async Task RestartPanelLaterAsync()
	{
		await Task.Delay(restartDelay).ConfigureAwait(false);
		try
		{
			using var panel = new ServiceController(PanelService);
			if (panel.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
			{
				panel.Stop();
			}

			panel.WaitForStatus(ServiceControllerStatus.Stopped, panelStopWait);
			panel.Start();
		}
		catch (Exception exception) when (exception is InvalidOperationException or System.ServiceProcess.TimeoutException
			or System.ComponentModel.Win32Exception)
		{
			// Ответ уже отправлен, сообщить об ошибке некому. Служба с автоматическим
			// восстановлением поднимется сама, иначе оператор увидит, что панель недоступна.
		}
	}
}
