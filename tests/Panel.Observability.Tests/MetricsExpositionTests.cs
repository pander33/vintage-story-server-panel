using System.Text.Json.Nodes;
using Panel.Observability.Domain;
using Panel.SharedKernel;

namespace Panel.Observability.Tests;

/// <summary>Текст <c>/metrics</c> для Prometheus (findings §82).</summary>
public sealed class MetricsExpositionTests
{
	private static readonly JsonObject latest = (JsonObject) JsonNode.Parse("""
		{
		  "active": true, "memory": 2147483648, "memory_total": 4294967296, "cpu_percent": 12.5,
		  "disk_used": 100, "disk_total": 1000, "disk_free": 900, "world_size": 5000, "restarts": 3, "uptime": 60,
		  "last_backup": {"name": "b.tar.gz", "modified": 1790000000},
		  "game": {"available": true, "age_seconds": 2, "observed_tps": 29.5, "average_tick_gap_ms": 33.9,
		           "players": [{"name": "Alice", "ip": "203.0.113.7"}, {"name": "Bob", "ip": "203.0.113.8"}]}
		}
		""")!;

	[Fact]
	public void Замер_сервера_в_формате_Prometheus()
	{
		var text = Export(ServerMetricsSample.FromLatest(InstanceId.Parse("main"), latest, "ready"));

		Assert.Contains("vs_panel_build_info{version=\"0.4.0+abc\"} 1\n", text, StringComparison.Ordinal);
		Assert.Contains("vs_server_health{server=\"main\",state=\"ready\"} 1\n", text, StringComparison.Ordinal);
		Assert.Contains("vs_server_ready{server=\"main\"} 1\n", text, StringComparison.Ordinal);
		Assert.Contains("vs_server_up{server=\"main\"} 1\n", text, StringComparison.Ordinal);
		Assert.Contains("vs_server_players{server=\"main\"} 2\n", text, StringComparison.Ordinal);
		Assert.Contains("vs_server_ticks_per_second{server=\"main\"} 29.5\n", text, StringComparison.Ordinal);
		Assert.Contains("vs_server_memory_bytes{server=\"main\"} 2147483648\n", text, StringComparison.Ordinal);
		Assert.Contains("vs_server_last_backup_timestamp_seconds{server=\"main\"} 1790000000\n", text, StringComparison.Ordinal);
		Assert.Contains("# TYPE vs_server_restarts_total counter\nvs_server_restarts_total{server=\"main\"} 3\n", text, StringComparison.Ordinal);
	}

	/// <summary>Метрики — только числа: ни имён игроков, ни их адресов (INV-38).</summary>
	[Fact]
	public void Без_имён_и_адресов_игроков()
	{
		var text = Export(ServerMetricsSample.FromLatest(InstanceId.Parse("main"), latest, "ready"));

		Assert.DoesNotContain("Alice", text, StringComparison.Ordinal);
		Assert.DoesNotContain("203.0.113", text, StringComparison.Ordinal);
	}

	/// <summary>
	/// Без замера сервер виден только состоянием: нуль вместо неизвестного выглядел бы как
	/// «ноль игроков и пустой диск».
	/// </summary>
	[Fact]
	public void Без_замера_только_состояние()
	{
		var text = Export(ServerMetricsSample.FromLatest(InstanceId.Parse("spare"), null, "unknown"));

		Assert.Contains("vs_server_health{server=\"spare\",state=\"unknown\"} 1\n", text, StringComparison.Ordinal);
		Assert.Contains("vs_server_ready{server=\"spare\"} 0\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain("vs_server_players{server=\"spare\"}", text, StringComparison.Ordinal);
		Assert.DoesNotContain("vs_server_up{server=\"spare\"}", text, StringComparison.Ordinal);
	}

	[Fact]
	public void Остановленная_игра_без_игровых_метрик()
	{
		var stopped = (JsonObject) latest.DeepClone();
		stopped["active"] = false;
		stopped["game"] = new JsonObject { ["available"] = false };

		var text = Export(ServerMetricsSample.FromLatest(InstanceId.Parse("main"), stopped, "stopped"));

		Assert.Contains("vs_server_up{server=\"main\"} 0\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain("vs_server_players{server=\"main\"}", text, StringComparison.Ordinal);
		Assert.DoesNotContain("vs_server_ticks_per_second{server=\"main\"}", text, StringComparison.Ordinal);
	}

	private static string Export(ServerMetricsSample sample) =>
		new MetricsExposition("0.4.0+abc", [sample]).ToText();
}
