using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.Provisioning.Domain;
using Panel.Provisioning.Ports;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Удаление сервера целиком на стороне панели.
/// </summary>
/// <remarks>
/// <para>
/// Демон стирает то, что принадлежит серверу: юнит и дерево. Метрики, история телеметрии и
/// записи прогонов лежат в каталоге состояния панели, и без этого шага они остались бы после
/// удаления сервера.
/// </para>
/// <para>
/// Монитор отпускается до удаления каталога, иначе очередной замер снова создал бы только что
/// стёртое. Если демон вернул ошибку, на стороне панели ничего не меняется: сервер и его
/// история остаются.
/// </para>
/// <para>
/// Сервер <c>PANEL_INSTANCE</c> удаляется так же, но сначала панель переходит на другой: к нему
/// она обращается и без реестра. Переход идёт до удаления, потому что он безвреден и при отказе
/// демона — новый сервер по умолчанию тоже зарегистрирован.
/// </para>
/// </remarks>
public sealed class InstanceRemoval
{
	public static Phrase BusyRefusal { get; } = Phrase.From("Дождитесь завершения обслуживания или обновления этого сервера");

	private readonly IRegistryAdministration registry;
	private readonly IProvisioningHost host;
	private readonly MaintenanceCoordinator coordinator;
	private readonly ServerMonitors monitors;
	private readonly PanelInstance panelInstance;
	private readonly PanelOptions options;

	public InstanceRemoval(
		IRegistryAdministration registry,
		IProvisioningHost host,
		MaintenanceCoordinator coordinator,
		ServerMonitors monitors,
		PanelInstance panelInstance,
		PanelOptions options)
	{
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(host);
		ArgumentNullException.ThrowIfNull(coordinator);
		ArgumentNullException.ThrowIfNull(monitors);
		ArgumentNullException.ThrowIfNull(panelInstance);
		ArgumentNullException.ThrowIfNull(options);

		this.registry = registry;
		this.host = host;
		this.coordinator = coordinator;
		this.monitors = monitors;
		this.panelInstance = panelInstance;
		this.options = options;
	}

	/// <summary>Проверки по порядку: фраза, занятость, переход с PANEL_INSTANCE, и только потом демон.</summary>
	public async Task<Result<InstanceDeletedPayload>> DeleteAsync(
		InstanceId instance,
		string? confirmation,
		CancellationToken ct)
	{
		var confirmed = Confirmation.ToDelete(instance.Value).Check(confirmation);
		if (!confirmed.IsSuccess)
		{
			return confirmed.Failure!;
		}

		if (coordinator.IsBusy(instance))
		{
			return Failure.Conflict(BusyRefusal);
		}

		var moved = await LeaveAsync(instance, ct).ConfigureAwait(false);
		if (!moved.IsSuccess)
		{
			return moved.Failure!;
		}

		var deleted = await registry.DeleteAsync(instance, ct).ConfigureAwait(false);
		if (!deleted.IsSuccess)
		{
			return deleted;
		}

		monitors.Release(instance);
		ForgetState(instance);
		return deleted;
	}

	/// <summary>
	/// Если <paramref name="instance"/> — сервер панели по умолчанию, перевести <c>PANEL_INSTANCE</c>
	/// на другой зарегистрированный сервер: умолчание реестра, иначе первый по имени. Нужен и
	/// перед «забыть».
	/// </summary>
	public async Task<Result<Done>> LeaveAsync(InstanceId instance, CancellationToken ct)
	{
		if (!panelInstance.Is(instance))
		{
			return Done.Ok;
		}

		var listing = await registry.ListAsync(ct).ConfigureAwait(false);
		if (!listing.IsSuccess)
		{
			return listing.Failure!;
		}

		var registered = listing.Value.Instances
			.Where(entry => entry.Valid)
			.Select(entry => InstanceId.Create(entry.Id))
			.Where(id => id.IsSuccess)
			.Select(id => id.Value)
			.ToList();
		var removable = RegistryRules.CanRemove(registered, instance);
		if (!removable.IsSuccess)
		{
			return removable.Failure!;
		}

		var declared = InstanceId.Create(listing.Value.Default);
		var successor = RegistryRules.DefaultAfterRemoving(registered, declared.IsSuccess ? declared.Value : null, instance);
		var setting = PanelSetting.Create("PANEL_INSTANCE", successor.Value);
		if (!setting.IsSuccess)
		{
			return setting.Failure!;
		}

		var written = await host.SetPanelEnvironmentAsync(setting.Value, ct).ConfigureAwait(false);
		if (!written.IsSuccess)
		{
			return Failure.Unavailable($"Сервер по умолчанию не переключён на {successor.Value}: {written.Failure!.Text}");
		}

		panelInstance.MoveTo(successor);
		return Done.Ok;
	}

	/// <summary>
	/// Каталог панели <c>instances/&lt;id&gt;</c>. Родитель сверяется дополнительно к проверке
	/// идентификатора: удаляется только прямой потомок <c>instances</c>.
	/// </summary>
	private void ForgetState(InstanceId instance)
	{
		var instances = Path.GetFullPath(Path.Combine(options.StateDirectory, "instances"));
		var directory = Path.GetFullPath(options.InstanceDirectory(instance));
		if (!string.Equals(Path.GetDirectoryName(directory), instances, StringComparison.Ordinal))
		{
			return;
		}

		try
		{
			if (Directory.Exists(directory))
			{
				Directory.Delete(directory, recursive: true);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Сервер уже удалён, и остаток истории не повод для отказа.
		}
	}
}
