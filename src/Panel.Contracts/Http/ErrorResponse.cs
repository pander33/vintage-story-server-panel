using System.Text.Json.Serialization;
using Panel.SharedKernel;

namespace Panel.Contracts.Http;

/// <summary>
/// Тело отказа: <c>{"detail": "…"}</c>. Фронтенд показывает <c>detail</c> как есть; текст
/// переводит граница HTTP при записи ответа.
/// </summary>
public sealed record ErrorResponse
{
	[JsonPropertyName("detail")] public required Phrase Detail { get; init; }
}
