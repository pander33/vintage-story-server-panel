using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Panel.Contracts.Configuration;

/// <summary>
/// Ответ демона на <c>config-get</c>: документ и время изменения файла.
/// </summary>
public sealed record ServerConfigFilePayload
{
	[JsonPropertyName("config")] public JsonObject Config { get; init; } = [];

	/// <summary>
	/// Время изменения файла в наносекундах. В .NET точность 100 нс (тики <c>FileInfo</c>); метка
	/// служит ревизией шаблона мира и сравнивается только сама с собой, поэтому потеря младших
	/// разрядов не мешает.
	/// </summary>
	[JsonPropertyName("modified_ns")] public long ModifiedNanoseconds { get; init; }
}

public sealed record ConfigBackupPayload
{
	[JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
	[JsonPropertyName("size")] public long Size { get; init; }
	[JsonPropertyName("modified")] public long Modified { get; init; }
}

public sealed record ConfigBackupsPayload
{
	[JsonPropertyName("backups")] public IReadOnlyList<ConfigBackupPayload> Backups { get; init; } = [];
}

/// <summary>Ответ <c>config-put</c>. <c>backup</c> — полный путь.</summary>
public sealed record ConfigWritePayload
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;
	[JsonPropertyName("backup")] public string? Backup { get; init; }
	[JsonPropertyName("restart_required")] public bool RestartRequired { get; init; } = true;
}

/// <summary>Ответ <c>config-rollback</c>. <c>safety_backup</c> — имя, а не путь.</summary>
public sealed record ConfigRollbackPayload
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;
	[JsonPropertyName("restored")] public string Restored { get; init; } = string.Empty;
	[JsonPropertyName("safety_backup")] public string? SafetyBackup { get; init; }
	[JsonPropertyName("restart_required")] public bool RestartRequired { get; init; } = true;
}
