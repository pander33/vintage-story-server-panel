using System.Text.Json.Serialization;
using Panel.Contracts.Agent;
using Panel.Contracts.Http;

namespace Panel.Contracts;

/// <summary>Метаданные частых типизированных ответов; конвертеры языка остаются в options.</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AgentRequest))]
[JsonSerializable(typeof(AgentReply))]
[JsonSerializable(typeof(ServiceStatusPayload))]
[JsonSerializable(typeof(TelemetryPayload))]
[JsonSerializable(typeof(TelemetryResponse))]
[JsonSerializable(typeof(PerformanceResponse))]
[JsonSerializable(typeof(PlayersResponse))]
[JsonSerializable(typeof(HostResponse))]
internal partial class PanelJsonContext : JsonSerializerContext;
