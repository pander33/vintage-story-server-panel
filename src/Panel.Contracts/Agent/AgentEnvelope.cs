using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Panel.SharedKernel;

namespace Panel.Contracts.Agent;

/// <summary>Запрос к привилегированной стороне: одна строка JSON.</summary>
public sealed record AgentRequest
{
	[JsonPropertyName("v")]
	public int Version { get; init; } = AgentProtocol.Version;

	/// <summary>
	/// Сборка панели (<see cref="AgentProtocol.Build"/>). Умолчания нет намеренно: запрос старой
	/// панели без поля должен разобраться как «сборка неизвестна», а не как своя.
	/// </summary>
	[JsonPropertyName("build")]
	public string? Build { get; init; }

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

	/// <summary>Сборка демона; как и в запросе, без умолчания. Ставят фабрики ответа.</summary>
	[JsonPropertyName("build")]
	public string? Build { get; init; }

	[JsonPropertyName("ok")]
	public required bool Ok { get; init; }

	[JsonPropertyName("code")]
	public string? Code { get; init; }

	/// <summary>Русский текст отказа: его читают журнал и вызывающий, которому перевод не нужен.</summary>
	[JsonPropertyName("error")]
	public string? Error { get; init; }

	/// <summary>
	/// Тот же отказ как <see cref="SharedKernel.Phrase"/>: шаблон и значения. По ним граница HTTP
	/// панели переводит текст на язык запроса.
	/// </summary>
	[JsonPropertyName("phrase")]
	public Phrase? Phrase { get; init; }

	[JsonPropertyName("result")]
	public JsonNode? Result { get; init; }

	public static AgentReply Success(JsonNode? result) =>
		new() { Build = AgentProtocol.Build, Ok = true, Result = result };

	public static AgentReply Failure(string code, string message) => Failure(code, SharedKernel.Phrase.From(message));

	public static AgentReply Failure(string code, Phrase message) =>
		new() { Build = AgentProtocol.Build, Ok = false, Code = code, Error = message.ToString(), Phrase = message };

	[OverloadResolutionPriority(1)]
	public static AgentReply Failure(string code, ref PhraseHandler message) => Failure(code, message.ToPhrase());

	/// <summary>Отказ как <see cref="Phrase"/>; ответ без шаблона — готовым текстом.</summary>
	public Phrase FailureText(Phrase fallback) =>
		Phrase ?? (Error is null ? fallback : SharedKernel.Phrase.From(Error));
}
