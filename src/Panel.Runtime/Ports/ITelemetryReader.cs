using Panel.Runtime.Domain;

namespace Panel.Runtime.Ports;

/// <summary>
/// Чтение снимка телеметрии, который пишет игровой мод.
/// </summary>
/// <remarks>
/// По карте контекстов телеметрия относится к Observability, но <see cref="TelemetryReading"/>
/// — доменный тип Runtime: его принимает политика готовности. Порт можно перенести в
/// Observability, когда у него появится второй потребитель.
///
/// Ошибка чтения — значение <see cref="TelemetryReading.Unavailable"/>, а не исключение и не
/// Result.
/// </remarks>
public interface ITelemetryReader
{
	Task<TelemetryReading> ReadAsync(SharedKernel.InstanceId instance, CancellationToken ct);
}
