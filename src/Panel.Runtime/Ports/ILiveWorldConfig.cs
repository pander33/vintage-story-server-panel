using Panel.Contracts.ServerCommands;
using Panel.Runtime.Domain;
using Panel.SharedKernel;

namespace Panel.Runtime.Ports;

/// <summary>
/// Живая конфигурация работающего мира: чтение из телеметрии, изменение командой.
/// </summary>
/// <remarks>
/// Отдельный порт, а не метод <see cref="IServerRuntime"/>: эта команда не из каталога
/// консольных действий, и два правила сборки строки в одном методе однажды перепутались бы.
/// </remarks>
public interface ILiveWorldConfig
{
	/// <summary>Недоступная телеметрия — значение, а не отказ: форма показывает причину.</summary>
	Task<LiveWorldConfig> ReadAsync(InstanceId instance, CancellationToken ct);

	Task<Result<Done>> ChangeAsync(InstanceId instance, WorldConfigCommand command, CancellationToken ct);
}
