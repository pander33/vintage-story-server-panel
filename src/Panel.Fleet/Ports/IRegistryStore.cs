using Panel.Fleet.Domain;

namespace Panel.Fleet.Ports;

/// <summary>
/// Чтение реестра серверов. Запись выполняет привилегированная сторона через
/// <see cref="IRegistryAdministration"/>.
/// </summary>
public interface IRegistryStore
{
	Task<RegistrySnapshot> LoadAsync(CancellationToken ct);
}
