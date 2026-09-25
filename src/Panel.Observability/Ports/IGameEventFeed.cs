using Panel.Observability.Domain;
using Panel.SharedKernel;

namespace Panel.Observability.Ports;

/// <summary>Хвост ленты игровых событий и признак того, пишет ли её установленный мод.</summary>
public sealed record GameEventPage(IReadOnlyList<GameEvent> Events, bool Supported);

/// <summary>
/// Лента событий внутри игры. Файл лежит в игровом каталоге, читает его демон.
/// </summary>
public interface IGameEventFeed
{
	/// <summary>Последние события, не больше <paramref name="count"/> (1…500).</summary>
	Task<Result<GameEventPage>> ReadAsync(InstanceId instance, int count, CancellationToken ct);
}
