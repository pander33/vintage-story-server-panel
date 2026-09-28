using Panel.Contracts.Agent;
using Panel.Fleet.Ports;
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
/// </remarks>
public sealed class InstanceRemoval
{
	/// <summary>Сервер PANEL_INSTANCE панель использует и без реестра.</summary>
	public static Phrase PanelInstanceRefusal { get; } = Phrase.From(
		"Это сервер, выбранный панелью по умолчанию (PANEL_INSTANCE). " +
		"Его нельзя удалить: панель продолжит обращаться к нему и без реестра. " +
		"Смените PANEL_INSTANCE в настройках панели и перезапустите её.");

	public static Phrase BusyRefusal { get; } = Phrase.From("Дождитесь завершения обслуживания или обновления этого сервера");

	private readonly IRegistryAdministration registry;
	private readonly MaintenanceCoordinator coordinator;
	private readonly ServerMonitors monitors;
	private readonly PanelOptions options;

	public InstanceRemoval(
		IRegistryAdministration registry,
		MaintenanceCoordinator coordinator,
		ServerMonitors monitors,
		PanelOptions options)
	{
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(coordinator);
		ArgumentNullException.ThrowIfNull(monitors);
		ArgumentNullException.ThrowIfNull(options);

		this.registry = registry;
		this.coordinator = coordinator;
		this.monitors = monitors;
		this.options = options;
	}

	/// <summary>Проверки по порядку: фраза, PANEL_INSTANCE, занятость, и только потом демон.</summary>
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

		if (instance.Value == options.DefaultInstance)
		{
			return Failure.Invalid(PanelInstanceRefusal);
		}

		if (coordinator.IsBusy(instance))
		{
			return Failure.Conflict(BusyRefusal);
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
