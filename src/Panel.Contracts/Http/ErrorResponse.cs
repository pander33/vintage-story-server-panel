using System.Text.Json.Serialization;

namespace Panel.Contracts.Http;

/// <summary>
/// Тело ошибки в формате FastAPI: <c>{"detail": "…"}</c>. Форма заморожена: фронтенд читает
/// <c>detail</c> и показывает его пользователю как есть.
/// </summary>
public sealed record ErrorResponse
{
	[JsonPropertyName("detail")] public required string Detail { get; init; }
}
