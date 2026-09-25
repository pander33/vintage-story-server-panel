using Panel.Contracts.Agent;

namespace Panel.Agent.Transport;

/// <summary>
/// Группа операций, адресованных серверу <c>-</c>: перечень реестра, создание и удаление
/// серверов, окружение панели.
/// </summary>
/// <remarks>
/// Пара к <see cref="IServerOperations"/> с другой сигнатурой: у операции реестра нет
/// сервера, из которого выводятся пути.
/// </remarks>
public interface IRegistryOperations
{
	/// <summary>Принадлежит ли операция этой группе.</summary>
	bool Serves(string op);

	Task<AgentReply> ExecuteAsync(AgentRequest request, CancellationToken ct);
}
