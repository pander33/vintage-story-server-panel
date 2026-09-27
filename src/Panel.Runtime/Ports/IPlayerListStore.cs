using Panel.Runtime.Domain;
using Panel.SharedKernel;

namespace Panel.Runtime.Ports;

/// <summary>
/// Списки забаненных игроков и белого списка (интерфейс 2.1 §3). Читает файлы игры, а не ответ
/// консоли: белый список консоль не показывает, а остановленный сервер консоли не даёт вовсе.
/// </summary>
public interface IPlayerListStore
{
	Task<Result<PlayerLists>> ReadAsync(InstanceId instance, CancellationToken ct);
}
