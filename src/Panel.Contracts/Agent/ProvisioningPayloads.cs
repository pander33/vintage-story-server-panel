using System.Text.Json.Serialization;
using Panel.SharedKernel;

namespace Panel.Contracts.Agent;

/// <summary>Одна среда выполнения из <c>dotnet --list-runtimes</c>.</summary>
public sealed record DotnetRuntimePayload
{
	[JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

	[JsonPropertyName("major")] public int Major { get; init; }

	[JsonPropertyName("version")] public string Version { get; init; } = string.Empty;
}

/// <summary>
/// Ответ <c>runtime-probe</c>: какой .NET есть на машине. Игре нужна среда той же старшей
/// версии, под которую она собрана.
/// </summary>
public sealed record RuntimeProbePayload
{
	/// <summary>Путь к dotnet; пусто, если не найден.</summary>
	[JsonPropertyName("dotnet")] public string Dotnet { get; init; } = string.Empty;

	[JsonPropertyName("runtimes")] public IReadOnlyList<DotnetRuntimePayload> Runtimes { get; init; } = [];

	[JsonPropertyName("majors")] public IReadOnlyList<int> Majors { get; init; } = [];

	/// <summary>
	/// Путь к <c>setpriv</c>; пусто, если не найден. Новое в 2.0 (ADR-07): без него
	/// <c>--genconfig</c> пришлось бы запускать от root, поэтому создание отказывает заранее.
	/// </summary>
	[JsonPropertyName("setpriv")] public string Setpriv { get; init; } = string.Empty;

	/// <summary>
	/// Платформа демона: <c>linux</c> или <c>windows</c>. Новое в 2.0: мастер требует
	/// <c>setpriv</c> только там, где игру запускает демон от root.
	/// </summary>
	[JsonPropertyName("platform")] public string Platform { get; init; } = "linux";

	/// <summary>Причина неудачной проверки; есть только в ответе панели, не демона.</summary>
	[JsonPropertyName("detail")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public Phrase? Detail { get; init; }
}

/// <summary>Ответ <c>disk-free</c>: свободное место там, где будут создаваться серверы.</summary>
public sealed record DiskFreePayload
{
	[JsonPropertyName("path")] public string Path { get; init; } = string.Empty;

	[JsonPropertyName("total")] public long Total { get; init; }

	[JsonPropertyName("used")] public long Used { get; init; }

	[JsonPropertyName("free")] public long Free { get; init; }

	[JsonPropertyName("instances_root")] public string InstancesRoot { get; init; } = string.Empty;
}

/// <summary>Ответ <c>instances-root-set</c>: каталог готов и записан в настройки панели.</summary>
public sealed record InstancesRootSetPayload
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;

	[JsonPropertyName("instances_root")] public string InstancesRoot { get; init; } = string.Empty;

	/// <summary>Прежний каталог; совпадает с новым, если выбор не изменился.</summary>
	[JsonPropertyName("previous")] public string Previous { get; init; } = string.Empty;
}

/// <summary>Ответ <c>transfer-inspect</c>: пакет проверен, ничего не создано.</summary>
public sealed record TransferInspectionPayload
{
	[JsonPropertyName("active_world")] public string ActiveWorld { get; init; } = string.Empty;
	[JsonPropertyName("game_version")] public string GameVersion { get; init; } = string.Empty;
	[JsonPropertyName("manifest_sha256")] public string ManifestSha256 { get; init; } = string.Empty;
}

/// <summary>Ответ <c>instance-create</c>: дерево создано и сервер записан в реестр.</summary>
public sealed record InstanceCreatedPayload
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;

	[JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

	[JsonPropertyName("root")] public string Root { get; init; } = string.Empty;

	[JsonPropertyName("unit")] public string Unit { get; init; } = string.Empty;

	[JsonPropertyName("port")] public int Port { get; init; }

	/// <summary>Имя архива мода телеметрии, положенного в Mods; пусто, если архивов не нашлось.</summary>
	[JsonPropertyName("telemetry_mod")] public string TelemetryMod { get; init; } = string.Empty;

	[JsonPropertyName("service_install_required")] public bool ServiceInstallRequired { get; init; } = true;
}

/// <summary>Ответ <c>instance-service-install</c>: юнит записан и включён.</summary>
public sealed record ServiceInstalledPayload
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;

	[JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

	[JsonPropertyName("unit")] public string Unit { get; init; } = string.Empty;

	[JsonPropertyName("path")] public string Path { get; init; } = string.Empty;
}

/// <summary>Ответ <c>panel-env-set</c>: строка записана и действует после перезапуска панели.</summary>
public sealed record PanelEnvironmentSetPayload
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;

	[JsonPropertyName("key")] public string Key { get; init; } = string.Empty;

	[JsonPropertyName("panel_restart_required")] public bool PanelRestartRequired { get; init; } = true;

	/// <summary>
	/// Новое в 2.0: правило брандмауэра для порта панели не удалось привести к новому адресу или
	/// порту. Строка записана; <see langword="null"/> — с брандмауэром всё в порядке или он не менялся.
	/// </summary>
	[JsonPropertyName("firewall_warning")] public Phrase? FirewallWarning { get; init; }
}

/// <summary>
/// Ответ <c>panel-logs</c>: строки <c>journalctl --output=short-iso</c> юнита панели. Если
/// журнала нет, <c>supported: false</c> и причина в <c>detail</c>.
/// </summary>
public sealed record PanelLogsPayload
{
	[JsonPropertyName("lines")] public IReadOnlyList<string> Lines { get; init; } = [];

	[JsonPropertyName("supported")] public bool Supported { get; init; } = true;

	[JsonPropertyName("detail")] public string Detail { get; init; } = string.Empty;

	[JsonPropertyName("unit")] public string Unit { get; init; } = string.Empty;
}

/// <summary>Ответ <c>panel-restart</c>: перезапуск поставлен в очередь systemd (<c>--no-block</c>).</summary>
public sealed record PanelRestartPayload
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;

	[JsonPropertyName("unit")] public string Unit { get; init; } = string.Empty;
}
