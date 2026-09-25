using System.Text.Json.Serialization;

namespace Panel.Contracts.Agent;

/// <summary>Запрос к привилегированной стороне: одна строка JSON.</summary>
public sealed record AgentRequest
{
	[JsonPropertyName("v")]
	public int Version { get; init; } = AgentProtocol.Version;

	[JsonPropertyName("op")]
	public required string Op { get; init; }

	/// <summary>Идентификатор сервера или <c>-</c> для обращения к реестру.</summary>
	[JsonPropertyName("instance")]
	public required string Instance { get; init; }

	[JsonPropertyName("args")]
	public IReadOnlyList<string> Args { get; init; } = [];
}

/// <summary>
/// Ответ привилегированной стороны. Успех и отказ различаются полем, а не формой, поэтому
/// разбирать ответ может каждый вызывающий одинаково.
/// </summary>
public sealed record AgentReply
{
	[JsonPropertyName("v")]
	public int Version { get; init; } = AgentProtocol.Version;

	[JsonPropertyName("ok")]
	public required bool Ok { get; init; }

	[JsonPropertyName("code")]
	public string? Code { get; init; }

	[JsonPropertyName("error")]
	public string? Error { get; init; }

	[JsonPropertyName("result")]
	public System.Text.Json.Nodes.JsonNode? Result { get; init; }

	public static AgentReply Success(System.Text.Json.Nodes.JsonNode? result) =>
		new() { Ok = true, Result = result };

	public static AgentReply Failure(string code, string message) =>
		new() { Ok = false, Code = code, Error = message };
}
