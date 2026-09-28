using System.Text.Json.Serialization;
using Panel.SharedKernel;

namespace Panel.Contracts.Agent;

/// <summary>Строка журнала службы.</summary>
public sealed record LogEntryPayload
{
	[JsonPropertyName("ts")] public double Timestamp { get; init; }
	[JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
	[JsonPropertyName("priority")] public int Priority { get; init; } = 6;
	[JsonPropertyName("source")] public string Source { get; init; } = string.Empty;

	/// <summary>Запись целиком, как её печатает <c>journalctl --output=short-iso</c>.</summary>
	[JsonPropertyName("line")] public string Line { get; init; } = string.Empty;
}

/// <summary>Порция журнала и курсор, с которого продолжать.</summary>
public sealed record LogPagePayload
{
	[JsonPropertyName("entries")] public IReadOnlyList<LogEntryPayload> Entries { get; init; } = [];
	[JsonPropertyName("cursor")] public string Cursor { get; init; } = "tail";
}

/// <summary>Ответ консоли, прочитанный из журнала после отправки команды.</summary>
public sealed record ConsoleEchoPayload
{
	[JsonPropertyName("lines")] public IReadOnlyList<string> Lines { get; init; } = [];
	[JsonPropertyName("waited_ms")] public long WaitedMilliseconds { get; init; }
}

/// <summary>Доступность консоли: ответ команды <c>command-capabilities</c>.</summary>
public sealed record ConsoleCapabilityPayload
{
	[JsonPropertyName("transport")] public string Transport { get; init; } = "disabled";
	[JsonPropertyName("available")] public bool Available { get; init; }
	[JsonPropertyName("reason")] public Phrase? Reason { get; init; }
}

/// <summary>
/// Ответ операции, которой нечего вернуть, кроме факта выполнения.
/// </summary>
/// <remarks>
/// Форма <c>{"ok": true, …}</c>: пустой ответ нельзя было бы отличить от оборванного
/// соединения.
/// </remarks>
public sealed record AcknowledgedPayload
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;
	[JsonPropertyName("action")] public string? Action { get; init; }
}
