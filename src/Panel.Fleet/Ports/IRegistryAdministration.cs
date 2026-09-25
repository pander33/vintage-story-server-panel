using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Fleet.Ports;

/// <summary>
/// Изменение реестра и перечень со сведениями, которые видны только на диске сервера.
/// </summary>
/// <remarks>
/// Читает реестр и сама панель (<see cref="IRegistryStore"/>), а пишет только
/// привилегированная сторона: файл принадлежит root. Порт, адрес и версия лежат в игровом
/// каталоге, к которому у веб-процесса тоже нет доступа.
/// </remarks>
public interface IRegistryAdministration
{
	Task<Result<RegistryListingPayload>> ListAsync(CancellationToken ct);

	Task<Result<InstanceRenamedPayload>> RenameAsync(InstanceId instance, InstanceName name, CancellationToken ct);

	Task<Result<InstanceForgottenPayload>> ForgetAsync(InstanceId instance, CancellationToken ct);

	/// <summary>Остановить и снять юнит, стереть дерево, убрать из реестра; только для сервера, созданного панелью.</summary>
	Task<Result<InstanceDeletedPayload>> DeleteAsync(InstanceId instance, CancellationToken ct);
}
