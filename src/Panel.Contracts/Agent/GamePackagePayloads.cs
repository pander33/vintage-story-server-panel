using System.Text.Json.Serialization;

namespace Panel.Contracts.Agent;

/// <summary>Ответ <c>game-package-inspect</c>: пакет прочитан целиком, сервер в нём есть.</summary>
public sealed record GamePackageInspectionPayload
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;

	[JsonPropertyName("files")] public long Files { get; init; }

	[JsonPropertyName("unpacked_size")] public long UnpackedSize { get; init; }

	/// <summary>Каталог внутри архива, где лежит <c>VintagestoryServer.dll</c>; пусто, если это корень.</summary>
	[JsonPropertyName("root")] public string Root { get; init; } = string.Empty;
}

/// <summary>Ответ <c>game-update-apply</c>: снимок прежней программы и сведения о пакете.</summary>
public sealed record GameUpdateAppliedPayload
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;

	[JsonPropertyName("snapshot")] public required string Snapshot { get; init; }

	[JsonPropertyName("files")] public long Files { get; init; }

	[JsonPropertyName("unpacked_size")] public long UnpackedSize { get; init; }

	[JsonPropertyName("root")] public string Root { get; init; } = string.Empty;
}

/// <summary>Ответ <c>game-update-rollback</c>.</summary>
public sealed record GameRollbackPayload
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;

	[JsonPropertyName("restored")] public required string Restored { get; init; }
}

/// <summary>Ответ <c>panel-game-version</c>: версия записана в реестр у этого сервера.</summary>
public sealed record GameVersionRecordedPayload
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;

	[JsonPropertyName("game_version")] public required string GameVersion { get; init; }

	[JsonPropertyName("panel_restart_required")] public bool PanelRestartRequired { get; init; }
}

/// <summary>Снимок программы сервера — элемент <c>game_history</c> ответа <c>/api/updates</c>.</summary>
public sealed record ProgramSnapshotPayload
{
	[JsonPropertyName("name")] public required string Name { get; init; }

	[JsonPropertyName("size")] public long Size { get; init; }

	[JsonPropertyName("modified")] public long Modified { get; init; }
}

/// <summary>Ответ <c>game-history</c>: двадцать последних снимков, новые первыми.</summary>
public sealed record ProgramSnapshotsPayload
{
	[JsonPropertyName("backups")] public IReadOnlyList<ProgramSnapshotPayload> Backups { get; init; } = [];
}
