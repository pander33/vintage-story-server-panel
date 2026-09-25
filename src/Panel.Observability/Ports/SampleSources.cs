using System.Text.Json.Nodes;
using Panel.SharedKernel;

namespace Panel.Observability.Ports;

/// <summary>
/// Снимок телеметрии как есть: ответ демона на <c>telemetry</c>.
/// </summary>
/// <remarks>
/// Ошибка чтения возвращается не исключением и не <see cref="Result{T}"/>, а снимком
/// <c>{"available": false, "detail": …}</c>. Неудачный опрос тоже попадает в историю, и график
/// показывает провал, а не пропуск.
/// </remarks>
public interface ITelemetrySource
{
	Task<JsonObject> ReadAsync(InstanceId instance, CancellationToken ct);
}

/// <summary>
/// Замер для истории метрик, ответ демона на <c>metrics</c>: состояние службы, память, диск,
/// размер мира, последняя копия и снимок телеметрии.
/// </summary>
public interface IMetricsSource
{
	Task<Result<JsonObject>> ReadAsync(InstanceId instance, CancellationToken ct);
}
