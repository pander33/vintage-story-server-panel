using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Agent.Content;
using Panel.Agent.Runtime;
using Panel.Agent.Transport;
using Panel.Contracts;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;

namespace Panel.Agent.Telemetry;

/// <summary>
/// Замер для истории метрик.
/// </summary>
/// <remarks>
/// Ответ почти целиком уходит на вкладку «Обзор», поэтому собирается как объект JSON:
/// полей много, и большую часть панель только показывает.
/// </remarks>
public sealed class MetricsCollector : IServerOperations
{
	/// <summary>Предел времени обхода каталога мира.</summary>
	private static readonly TimeSpan sizeBudget = TimeSpan.FromSeconds(60);

	private readonly IServiceBackend service;
	private readonly TelemetryFileReader telemetry;
	private readonly BackupBackend backups;

	public MetricsCollector(IServiceBackend service, TelemetryFileReader telemetry, BackupBackend backups)
	{
		ArgumentNullException.ThrowIfNull(service);
		ArgumentNullException.ThrowIfNull(telemetry);
		ArgumentNullException.ThrowIfNull(backups);

		this.service = service;
		this.telemetry = telemetry;
		this.backups = backups;
	}

	bool IServerOperations.Serves(string op) => op == AgentProtocol.OpMetrics;

	async Task<AgentReply> IServerOperations.ExecuteAsync(AgentRequest request, RegisteredServer server, CancellationToken ct) =>
		AgentReply.Success(await CollectAsync(server, ct).ConfigureAwait(false));

	public async Task<JsonObject> CollectAsync(RegisteredServer server, CancellationToken ct)
	{
		var status = await service.StatusAsync(server, ct).ConfigureAwait(false);
		var value = JsonSerializer.SerializeToNode(status, PanelJson.Options)!.AsObject();

		var disk = GameFiles.Usage(server.DataDirectory);
		var warningBytes = (long) (Setting(server, "disk_warning_free_gb", 10) * 1024 * 1024 * 1024);
		var warningPercent = Setting(server, "disk_warning_free_percent", 10);
		var freePercent = disk.Total != 0 ? (double) disk.Free / disk.Total * 100 : 0;

		value["memory_total"] = MemoryTotal();
		value["disk_total"] = disk.Total;
		value["disk_used"] = disk.Used;
		value["disk_free"] = disk.Free;
		value["disk_warning"] = disk.Free < warningBytes || freePercent < warningPercent;
		value["world_size"] = GameFiles.DirectorySize(server.WorldDirectory, sizeBudget);
		value["current_world"] = await CurrentWorldAsync(server, ct).ConfigureAwait(false);
		value["last_backup"] = LastBackup(server);

		var telemetry = await this.telemetry.ReadAsync(server.TelemetryFile, ct).ConfigureAwait(false);
		value["game"] = JsonSerializer.SerializeToNode(telemetry, PanelJson.Options);

		return value;
	}

	/// <summary>Порог из записи реестра или умолчание.</summary>
	private static double Setting(RegisteredServer server, string key, double fallback) =>
		server.Raw?[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : fallback;

	/// <summary>
	/// Физическая память машины. На Linux — <c>MemTotal</c> из <c>/proc/meminfo</c>, на Windows
	/// объём сообщает сборщик мусора.
	/// </summary>
	private static long MemoryTotal()
	{
		if (!OperatingSystem.IsLinux())
		{
			return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
		}

		try
		{
			foreach (var line in File.ReadLines("/proc/meminfo"))
			{
				if (!line.StartsWith("MemTotal:", StringComparison.Ordinal))
				{
					continue;
				}

				var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
				return parts.Length > 1 && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var kilobytes)
					? kilobytes * 1024
					: 0;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return 0;
		}

		return 0;
	}

	/// <summary>
	/// Мир, который запустит сервер: <c>WorldConfig.WorldName</c> и имя файла из
	/// <c>SaveFileLocation</c>. Нечитаемые настройки не считаются ошибкой замера.
	/// </summary>
	private static async Task<JsonObject?> CurrentWorldAsync(RegisteredServer server, CancellationToken ct)
	{
		try
		{
			var config = JsonNode.Parse(await File.ReadAllTextAsync(server.ConfigFile, ct).ConfigureAwait(false));
			if (config is not JsonObject document || document["WorldConfig"] is not JsonObject world)
			{
				return null;
			}

			var save = world["SaveFileLocation"] is JsonValue location && location.GetValueKind() == JsonValueKind.String
				? Path.GetFileName(location.GetValue<string>().TrimEnd('/', '\\'))
				: string.Empty;

			return new JsonObject
			{
				["name"] = world["WorldName"]?.DeepClone(),
				["save"] = save.Length > 0 ? save : null,
			};
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return null;
		}
	}

	private JsonObject? LastBackup(RegisteredServer server)
	{
		var set = backups.List(server);
		if (!set.IsSuccess || set.Value.Backups.Count == 0)
		{
			return null;
		}

		var latest = set.Value.Backups[0];
		return new JsonObject
		{
			["name"] = latest.Name.Value,
			["size"] = latest.Size,
			["modified"] = latest.Modified.ToUnixTimeSeconds(),
		};
	}
}
