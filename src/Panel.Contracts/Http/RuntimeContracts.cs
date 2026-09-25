using System.Text.Json.Serialization;
using Panel.Contracts.ServerCommands;

namespace Panel.Contracts.Http;

public sealed record ServiceActionRequest
{
	[JsonPropertyName("action")] public string Action { get; init; } = string.Empty;
}

public sealed record ServiceActionResponse
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;
	[JsonPropertyName("action")] public required string Action { get; init; }
}

/// <summary>
/// Ответ <c>GET /api/logs</c>: строки, разобранные события и сводка сбоев.
/// </summary>
public sealed record LogsResponse
{
	[JsonPropertyName("lines")] public required IReadOnlyList<string> Lines { get; init; }
	[JsonPropertyName("events")] public required IReadOnlyList<LogEventResponse> Events { get; init; }
	[JsonPropertyName("crashes")] public required CrashSummaryResponse Crashes { get; init; }
}

/// <summary>Событие журнала.</summary>
public sealed record LogEventResponse
{
	[JsonPropertyName("line")] public required string Line { get; init; }
	[JsonPropertyName("category")] public required string Category { get; init; }
	[JsonPropertyName("player_event")] public PlayerChangeResponse? PlayerEvent { get; init; }
	[JsonPropertyName("overload")] public bool Overload { get; init; }
	[JsonPropertyName("cursor")] public string? Cursor { get; init; }
	[JsonPropertyName("timestamp")] public long? Timestamp { get; init; }
}

public sealed record PlayerChangeResponse
{
	[JsonPropertyName("type")] public required string Type { get; init; }
	[JsonPropertyName("player")] public required string Player { get; init; }
}

public sealed record CrashSummaryResponse
{
	[JsonPropertyName("count")] public int Count { get; init; }
	[JsonPropertyName("repeated")] public bool Repeated { get; init; }
	[JsonPropertyName("last")] public string? Last { get; init; }
}

/// <summary>Каталог разрешённых консольных действий для интерфейса.</summary>
public sealed record CommandCatalogueResponse
{
	[JsonPropertyName("commands")] public required IReadOnlyList<ServerCommandView> Commands { get; init; }
	[JsonPropertyName("game_modes")] public required IReadOnlyList<string> GameModes { get; init; }
	[JsonPropertyName("console")] public required ConsoleStateResponse Console { get; init; }

	/// <summary>
	/// Состояние консоли в форме <c>{"commands", "transport", "detail"}</c>. Фронтенд читает именно
	/// его (<c>data.capability.commands</c>); <see cref="Console"/> оставлено как дополнительное
	/// поле.
	/// </summary>
	[JsonPropertyName("capability")] public required CommandCapabilityResponse Capability { get; init; }
}

/// <summary>Ответ <c>command-capabilities</c>.</summary>
public sealed record CommandCapabilityResponse
{
	[JsonPropertyName("commands")] public required bool Commands { get; init; }
	[JsonPropertyName("transport")] public required string Transport { get; init; }
	[JsonPropertyName("detail")] public required string Detail { get; init; }
}

/// <summary>
/// Действие для интерфейса. Область значений разворачивается в описание, по которому
/// фронтенд рисует элемент управления: список, число или время суток.
/// </summary>
public sealed record ServerCommandView
{
	[JsonPropertyName("action")] public required string Action { get; init; }
	[JsonPropertyName("group")] public required string Group { get; init; }
	[JsonPropertyName("label")] public required string Label { get; init; }
	[JsonPropertyName("hint")] public required string Hint { get; init; }
	[JsonPropertyName("reply")] public required bool Reply { get; init; }
	[JsonPropertyName("confirm")] public string? Confirm { get; init; }
	[JsonPropertyName("value")] public ValueDomainView? Value { get; init; }

	public static ServerCommandView Of(ServerCommandSpec spec) => new()
	{
		Action = spec.Action,
		Group = spec.Group,
		Label = spec.Label,
		Hint = spec.Hint,
		Reply = spec.ReadsBack,
		Confirm = spec.ConfirmPrompt,
		Value = ValueDomainView.Of(spec.Value),
	};
}

public sealed record ValueDomainView
{
	/// <summary>choice | number | time: какой элемент нарисовать.</summary>
	[JsonPropertyName("kind")] public required string Kind { get; init; }

	[JsonPropertyName("label")] public required string Label { get; init; }
	[JsonPropertyName("options")] public IReadOnlyList<string>? Options { get; init; }
	[JsonPropertyName("min")] public decimal? Min { get; init; }
	[JsonPropertyName("max")] public decimal? Max { get; init; }
	[JsonPropertyName("step")] public decimal? Step { get; init; }

	/// <summary>Начальное значение поля строкой.</summary>
	[JsonPropertyName("default")] public string? Default { get; init; }

	public static ValueDomainView? Of(ValueDomain? domain) => domain switch
	{
		ValueDomain.Choice choice => new ValueDomainView
		{
			Kind = "choice",
			Label = choice.Label,
			Options = choice.Options,
		},
		ValueDomain.Number number => new ValueDomainView
		{
			Kind = "number",
			Label = number.Label,
			Min = number.Min,
			Max = number.Max,
			Step = number.Step,
			Default = number.Default,
		},

		// Время суток передаётся как "choice": фронтенд рисует числовое поле для любого вида,
		// кроме "choice", и вид "time" превратил бы список пресетов в поле ввода. Границы часа
		// идут дополнительными полями: демон принимает час, но интерфейс его пока не предлагает.
		ValueDomain.TimeOfDay time => new ValueDomainView
		{
			Kind = "choice",
			Label = time.Label,
			Options = time.Presets,
			Min = 0,
			Max = 23.99m,
			Step = 0.01m,
		},
		_ => null,
	};
}

public sealed record ConsoleStateResponse
{
	[JsonPropertyName("transport")] public required string Transport { get; init; }
	[JsonPropertyName("available")] public required bool Available { get; init; }
	[JsonPropertyName("detail")] public string? Detail { get; init; }
}

public sealed record ServerCommandResponse
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;
	[JsonPropertyName("action")] public required string Action { get; init; }
	[JsonPropertyName("command")] public required string Command { get; init; }
	[JsonPropertyName("reply")] public required IReadOnlyList<string> Reply { get; init; }
}

/// <summary>Тело <c>POST /api/server/console</c>, маршрута 2.0 (ADR-17).</summary>
public sealed record RawConsoleRequest
{
	[JsonPropertyName("command")] public string Command { get; init; } = string.Empty;
}

/// <summary>Ответ <c>POST /api/server/console</c>: отправленная строка и что сервер вывел после неё.</summary>
public sealed record RawConsoleResponse
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;
	[JsonPropertyName("command")] public required string Command { get; init; }
	[JsonPropertyName("reply")] public required IReadOnlyList<string> Reply { get; init; }
}

/// <summary>Тело <c>POST /api/players/action</c>.</summary>
public sealed record PlayerCommandRequest
{
	[JsonPropertyName("action")] public string Action { get; init; } = string.Empty;
	[JsonPropertyName("player")] public string Player { get; init; } = string.Empty;
	[JsonPropertyName("detail")] public string Detail { get; init; } = string.Empty;
}
