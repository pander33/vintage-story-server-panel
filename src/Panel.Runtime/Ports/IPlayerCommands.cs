using Panel.Contracts.Agent;
using Panel.Contracts.ServerCommands;
using Panel.SharedKernel;

namespace Panel.Runtime.Ports;

/// <summary>
/// Действия над игроками через консоль.
/// </summary>
/// <remarks>
/// Отдельно от <see cref="IServerRuntime"/> по той же причине, что и
/// <see cref="ILiveWorldConfig"/>: у них своё правило сборки строки и свой набор действий.
/// </remarks>
public interface IPlayerCommands
{
	Task<Result<PlayerActionPayload>> SendAsync(InstanceId instance, PlayerCommand command, CancellationToken ct);
}
