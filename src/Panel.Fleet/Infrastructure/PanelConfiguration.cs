using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.SharedKernel;

namespace Panel.Fleet.Infrastructure;

/// <summary>
/// Файл настроек панели <c>/etc/vs-panel.json</c>. Поддерживаются обе его версии.
/// </summary>
/// <remarks>
/// Версия 1 — плоский файл: один сервер, описанный прямо в корне объекта. Из него получается
/// единственная запись с идентификатором <c>main</c>, доступная только для чтения. Версия 2
/// указывает на отдельный файл реестра.
/// </remarks>
public sealed record PanelConfiguration
{
	public required int Version { get; init; }

	public required PlatformLayout Layout { get; init; }

	/// <summary>Путь к реестру. Пуст для версии 1: там реестра как файла нет.</summary>
	public string InstancesFile { get; init; } = string.Empty;

	public string InstancesRoot { get; init; } = string.Empty;

	/// <summary>
	/// Прежние каталоги серверов (<c>former_instances_roots</c>). Сервер, созданный в прежнем
	/// каталоге, по-прежнему считается созданным панелью: его можно удалить и переустановить.
	/// </summary>
	public IReadOnlyList<string> FormerInstancesRoots { get; init; } = [];

	public string StagingDirectory { get; init; } = string.Empty;

	public string PanelEnvFile { get; init; } = string.Empty;

	/// <summary>Архивы мода телеметрии от установщика, из которых мод получает новый сервер.</summary>
	public string TelemetryDirectory { get; init; } = string.Empty;

	/// <summary>Каталог выполнения, в котором лежат FIFO консолей серверов (<c>/run</c>).</summary>
	public string RunDirectory { get; init; } = string.Empty;

	/// <summary>Плоская запись версии 1, если файл был версии 1.</summary>
	public JsonObject? FlatInstance { get; init; }

	/// <summary>Запись реестра запрещена: версия 1 только читается.</summary>
	public bool IsReadOnlyRegistry => Version < 2;

	public static Result<PanelConfiguration> Parse(string json)
	{
		JsonNode? node;
		try
		{
			node = JsonNode.Parse(json);
		}
		catch (JsonException exception)
		{
			return Failure.Invalid($"Не удалось разобрать настройки панели: {exception.Message}");
		}

		if (node is not JsonObject root)
		{
			return Failure.Invalid("Настройки панели не являются объектом JSON");
		}

		var version = (int?) root["version"] ?? 1;
		var layout = (string?) root["layout"] == "windows" ? PlatformLayout.Windows : PlatformLayout.Linux;

		// Умолчания раскладки: в файле любой версии ключа может не быть, и тогда путь берётся из
		// раскладки.
		var defaults = layout == PlatformLayout.Windows ? windowsDefaults : linuxDefaults;
		string Path(string key) => (string?) root[key] is { Length: > 0 } value ? value : defaults[key];

		if (version < 2)
		{
			return new PanelConfiguration
			{
				Version = version,
				Layout = layout,
				FlatInstance = root,
				InstancesRoot = Path("instances_root"),
				FormerInstancesRoots = Former(root),
				StagingDirectory = Path("staging_dir"),
				PanelEnvFile = Path("panel_env_file"),
				TelemetryDirectory = Path("telemetry_dir"),
				RunDirectory = Path("run_dir"),
			};
		}

		var instancesFile = (string?) root["instances_file"] ?? string.Empty;
		if (instancesFile.Length == 0)
		{
			return Failure.Invalid("В настройках панели версии 2 не указан instances_file");
		}

		return new PanelConfiguration
		{
			Version = version,
			Layout = layout,
			InstancesFile = instancesFile,
			InstancesRoot = Path("instances_root"),
			FormerInstancesRoots = Former(root),
			StagingDirectory = Path("staging_dir"),
			PanelEnvFile = Path("panel_env_file"),
			TelemetryDirectory = Path("telemetry_dir"),
			RunDirectory = Path("run_dir"),
		};
	}

	private static string[] Former(JsonObject root) =>
		root["former_instances_roots"] is JsonArray items
			? [.. items.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : null).OfType<string>().Where(text => text.Length > 0)]
			: [];

	private static readonly Dictionary<string, string> linuxDefaults = new(StringComparer.Ordinal)
	{
		["instances_root"] = "/srv/vs-panel",
		["telemetry_dir"] = "/usr/local/share/vs-panel/telemetry",
		["staging_dir"] = "/var/lib/vs-panel/staging",
		["panel_env_file"] = "/etc/vs-panel.env",
		["run_dir"] = "/run",
	};

	private static readonly Dictionary<string, string> windowsDefaults = new(StringComparer.Ordinal)
	{
		["instances_root"] = @"C:\ProgramData\vs-panel\servers",
		["telemetry_dir"] = @"C:\ProgramData\vs-panel\telemetry",
		["staging_dir"] = @"C:\ProgramData\vs-panel\state\staging",
		["panel_env_file"] = @"C:\ProgramData\vs-panel\vs-panel.env",
		["run_dir"] = @"C:\ProgramData\vs-panel\run",
	};
}
