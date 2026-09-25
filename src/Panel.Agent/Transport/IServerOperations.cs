using Panel.Contracts.Agent;
using Panel.Fleet.Domain;

namespace Panel.Agent.Transport;

/// <summary>
/// Группа операций над одним сервером, вынесенная из таблицы диспетчера: содержимое,
/// настройки, обновление игры, замер метрик.
/// </summary>
/// <remarks>
/// Интерфейс нужен для платформ: диспетчер не должен ссылаться на типы Linux, чтобы
/// собираться для обеих платформ.
/// </remarks>
public interface IServerOperations
{
	/// <summary>Принадлежит ли операция этой группе.</summary>
	bool Serves(string op);

	Task<AgentReply> ExecuteAsync(AgentRequest request, RegisteredServer server, CancellationToken ct);
}
