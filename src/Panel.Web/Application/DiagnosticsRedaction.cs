using System.Collections.Frozen;
using System.Text.Json.Nodes;
using Panel.Contracts.Privacy;

namespace Panel.Web.Application;

/// <summary>
/// Очистка диагностического пакета.
/// </summary>
/// <remarks>
/// Пакет скачивают, чтобы переслать (в чат, в задачу, другому человеку), поэтому очистка
/// одинакова для всех ролей. Значение чувствительного ключа заменяется целиком, в остальных
/// строках скрываются адреса.
/// </remarks>
public static class DiagnosticsRedaction
{
	public const string Redacted = "[redacted]";

	/// <summary>Чувствительные ключи; сравниваются без учёта регистра.</summary>
	public static readonly FrozenSet<string> SensitiveKeys = FrozenSet.Create(
		StringComparer.OrdinalIgnoreCase,
		"ip", "token", "password", "secret", "chat_id", "uid", "client_id");

	/// <summary>Возвращает новое дерево, исходное не меняется.</summary>
	public static JsonNode? Redact(JsonNode? value) => value switch
	{
		JsonObject item => new JsonObject(item.Select(pair => KeyValuePair.Create(
			pair.Key,
			SensitiveKeys.Contains(pair.Key) ? (JsonNode?) JsonValue.Create(Redacted) : Redact(pair.Value)))),
		JsonArray items => new JsonArray([.. items.Select(Redact)]),
		JsonValue text when text.TryGetValue<string>(out var line) => JsonValue.Create(AddressMask.Scrub(line, hide: true)),
		_ => value?.DeepClone(),
	};
}
