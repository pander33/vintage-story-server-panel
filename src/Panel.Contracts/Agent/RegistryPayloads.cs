using System.Text.Json.Serialization;

namespace Panel.Contracts.Agent;

/// <summary>
/// Сервер в перечне реестра. У испорченной записи есть только первые поля, <c>valid: false</c>
/// и <c>detail</c>.
/// </summary>
public sealed record RegistryEntryPayload
{
	[JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
	[JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
	[JsonPropertyName("default")] public bool Default { get; init; }
	[JsonPropertyName("game_version")] public string GameVersion { get; init; } = string.Empty;
	[JsonPropertyName("port")] public int Port { get; init; }
	[JsonPropertyName("ip")] public string Ip { get; init; } = string.Empty;
	[JsonPropertyName("valid")] public bool Valid { get; init; } = true;
	[JsonPropertyName("detail")] public string? Detail { get; init; }
	[JsonPropertyName("unit")] public string? Unit { get; init; }
	[JsonPropertyName("data_dir")] public string? DataDirectory { get; init; }
	[JsonPropertyName("program_dir")] public string? ProgramDirectory { get; init; }
	[JsonPropertyName("active")] public bool? Active { get; init; }
}

public sealed record RegistryListingPayload
{
	[JsonPropertyName("instances")] public IReadOnlyList<RegistryEntryPayload> Instances { get; init; } = [];
	[JsonPropertyName("default")] public string? Default { get; init; }
	[JsonPropertyName("writable")] public bool Writable { get; init; }
}

public sealed record InstanceRenamedPayload
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;
	[JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
	[JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
}

/// <summary>Ответ «забыть»: файлы остаются, в ответе указано, где они.</summary>
public sealed record InstanceForgottenPayload
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;
	[JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
	[JsonPropertyName("default")] public string Default { get; init; } = string.Empty;
	[JsonPropertyName("data_dir")] public string DataDirectory { get; init; } = string.Empty;
	[JsonPropertyName("program_dir")] public string ProgramDirectory { get; init; } = string.Empty;
	[JsonPropertyName("files_kept")] public bool FilesKept { get; init; } = true;
}

/// <summary>Ответ «удалить».</summary>
public sealed record InstanceDeletedPayload
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;
	[JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
	[JsonPropertyName("root")] public string Root { get; init; } = string.Empty;
	[JsonPropertyName("unit")] public string Unit { get; init; } = string.Empty;
	[JsonPropertyName("default")] public string Default { get; init; } = string.Empty;
	[JsonPropertyName("deleted")] public bool Deleted { get; init; } = true;
}
