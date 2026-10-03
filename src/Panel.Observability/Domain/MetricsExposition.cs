using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Panel.SharedKernel;

namespace Panel.Observability.Domain;

/// <summary>
/// Последний замер одного сервера в виде, нужном Prometheus: только числа, без имён и адресов
/// игроков (INV-38).
/// </summary>
public sealed record ServerMetricsSample
{
	public required InstanceId Server { get; init; }

	/// <summary>Состояние готовности в форме протокола: <c>ready</c>, <c>stopped</c>, <c>hung</c>…</summary>
	public required string Health { get; init; }

	public bool? Active { get; init; }

	public double? CpuPercent { get; init; }

	public long? MemoryBytes { get; init; }

	public long? HostMemoryBytes { get; init; }

	public long? DiskUsedBytes { get; init; }

	public long? DiskTotalBytes { get; init; }

	public long? DiskFreeBytes { get; init; }

	public long? WorldSizeBytes { get; init; }

	public long? Restarts { get; init; }

	public long? UptimeSeconds { get; init; }

	public int? Players { get; init; }

	public double? TicksPerSecond { get; init; }

	public double? TickGapMilliseconds { get; init; }

	public long? TelemetryAgeSeconds { get; init; }

	public long? LastBackupSeconds { get; init; }

	/// <summary>
	/// Из последнего ответа демона на замер (<see cref="MetricsHistory.Latest"/>). Без замера —
	/// только состояние готовности: сервер есть в реестре, но о нём ещё ничего не известно.
	/// </summary>
	public static ServerMetricsSample FromLatest(InstanceId server, JsonObject? latest, string health)
	{
		ArgumentNullException.ThrowIfNull(health);

		if (latest is null)
		{
			return new ServerMetricsSample { Server = server, Health = health };
		}

		var game = latest["game"] as JsonObject;
		var gameAvailable = game is not null && TelemetryNumbers.Truthy(game["available"]);

		return new ServerMetricsSample
		{
			Server = server,
			Health = health,
			Active = TelemetryNumbers.Truthy(latest["active"]),
			CpuPercent = Optional(latest["cpu_percent"]),
			MemoryBytes = Whole(latest["memory"]),
			HostMemoryBytes = Whole(latest["memory_total"]),
			DiskUsedBytes = Whole(latest["disk_used"]),
			DiskTotalBytes = Whole(latest["disk_total"]),
			DiskFreeBytes = Whole(latest["disk_free"]),
			WorldSizeBytes = Whole(latest["world_size"]),
			Restarts = Whole(latest["restarts"]),
			UptimeSeconds = Whole(latest["uptime"]),
			Players = gameAvailable && game!["players"] is JsonArray players ? players.Count : null,
			TicksPerSecond = gameAvailable ? Optional(game!["observed_tps"]) : null,
			TickGapMilliseconds = gameAvailable ? Optional(game!["average_tick_gap_ms"]) : null,
			TelemetryAgeSeconds = game is null ? null : Whole(game["age_seconds"]),
			LastBackupSeconds = latest["last_backup"] is JsonObject backup ? Whole(backup["modified"]) : null,
		};
	}

	private static double? Optional(JsonNode? node) =>
		node is JsonValue ? TelemetryNumbers.Number(node) : null;

	private static long? Whole(JsonNode? node) =>
		node is JsonValue ? (long) TelemetryNumbers.Number(node) : null;
}

/// <summary>
/// Текст для Prometheus (формат exposition 0.0.4): сборка панели и последний замер каждого
/// сервера реестра. Метка сервера — <c>server</c>, а не <c>instance</c>: ту Prometheus ставит
/// сам, адресом цели.
/// </summary>
public sealed record MetricsExposition(string Build, IReadOnlyList<ServerMetricsSample> Servers)
{
	public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

	public string ToText()
	{
		var text = new StringBuilder();

		Family(text, "vs_panel_build_info", "gauge", "Panel build; the value is always 1.");
		text.Append("vs_panel_build_info{version=\"").Append(Escape(Build)).Append("\"} 1\n");

		Family(text, "vs_server_health", "gauge", "Readiness verdict of the game server; the state is a label, the value is always 1.");
		foreach (var server in Servers)
		{
			text.Append("vs_server_health{server=\"").Append(Escape(server.Server.Value))
				.Append("\",state=\"").Append(Escape(server.Health)).Append("\"} 1\n");
		}

		Gauge(text, "vs_server_ready", "Whether the latest readiness verdict is ready (1) or not (0).", server => server.Health == "ready" ? 1 : 0);
		Gauge(text, "vs_server_up", "Whether the game server service is running (1) or not (0).", server => server.Active is { } active ? (active ? 1 : 0) : null);
		Gauge(text, "vs_server_players", "Players online.", server => server.Players);
		Gauge(text, "vs_server_ticks_per_second", "Observed server ticks per second.", server => server.TicksPerSecond);
		Gauge(text, "vs_server_tick_gap_milliseconds", "Average interval between server ticks.", server => server.TickGapMilliseconds);
		Gauge(text, "vs_server_telemetry_age_seconds", "Age of the latest game telemetry snapshot.", server => server.TelemetryAgeSeconds);
		Gauge(text, "vs_server_cpu_percent", "CPU used by the game server process, percent of one core (above 100 on several cores).", server => server.CpuPercent);
		Gauge(text, "vs_server_memory_bytes", "Memory used by the game server process.", server => server.MemoryBytes);
		Gauge(text, "vs_host_memory_bytes", "Physical memory of the machine running the game server.", server => server.HostMemoryBytes);
		Gauge(text, "vs_server_disk_used_bytes", "Used space on the volume with the server data.", server => server.DiskUsedBytes);
		Gauge(text, "vs_server_disk_free_bytes", "Free space on the volume with the server data.", server => server.DiskFreeBytes);
		Gauge(text, "vs_server_disk_total_bytes", "Size of the volume with the server data.", server => server.DiskTotalBytes);
		Gauge(text, "vs_server_world_size_bytes", "Size of the server world files.", server => server.WorldSizeBytes);
		Gauge(text, "vs_server_uptime_seconds", "Time since the game server process started.", server => server.UptimeSeconds);
		Gauge(text, "vs_server_last_backup_timestamp_seconds", "Modification time of the newest backup, Unix seconds.", server => server.LastBackupSeconds);
		Family(text, "vs_server_restarts_total", "counter", "Service restarts counted by the service manager.");
		Values(text, "vs_server_restarts_total", server => server.Restarts);

		return text.ToString();
	}

	private void Gauge(StringBuilder text, string name, string help, Func<ServerMetricsSample, double?> value)
	{
		Family(text, name, "gauge", help);
		Values(text, name, value);
	}

	private void Values(StringBuilder text, string name, Func<ServerMetricsSample, double?> value)
	{
		foreach (var server in Servers)
		{
			if (value(server) is { } number && double.IsFinite(number))
			{
				text.Append(name).Append("{server=\"").Append(Escape(server.Server.Value)).Append("\"} ")
					.Append(number.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
			}
		}
	}

	private static void Family(StringBuilder text, string name, string type, string help) =>
		text.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n')
			.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');

	/// <summary>Значение метки: обратная косая черта, кавычка и перевод строки экранируются.</summary>
	private static string Escape(string value) =>
		value.Replace("\\", "\\\\", StringComparison.Ordinal)
			.Replace("\"", "\\\"", StringComparison.Ordinal)
			.Replace("\n", "\\n", StringComparison.Ordinal);
}
