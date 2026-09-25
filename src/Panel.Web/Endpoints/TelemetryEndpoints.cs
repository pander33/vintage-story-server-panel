using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Panel.Access.Authorization;
using Panel.Contracts;
using Panel.Contracts.Http;
using Panel.Contracts.Privacy;
using Panel.Observability.Domain;
using Panel.Runtime.Domain;
using Panel.Runtime.Ports;
using Panel.SharedKernel;
using Panel.Web.Application;
using Panel.Web.Middleware;

namespace Panel.Web.Endpoints;

/// <summary>
/// Игроки, телеметрия, производительность и метрики: всё, что берётся из истории сэмплеров,
/// а не запрашивается у демона при каждом запросе.
/// </summary>
public static class TelemetryEndpoints
{
	/// <summary>Сколько строк журнала разбирается для игроков и производительности.</summary>
	private const int journalWindow = 2000;

	public static void MapTelemetry(this IEndpointRouteBuilder routes)
	{
		routes.MapGet("/api/players", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IServerRuntime runtime,
			ServerMonitors monitors,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadTelemetry), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var instance = Instance(http);
			var entries = await runtime.LogsAsync(instance, journalWindow, ct).ConfigureAwait(false);
			var status = await runtime.StatusAsync(instance, ct).ConfigureAwait(false);
			var capability = await runtime.CapabilitiesAsync(instance, ct).ConfigureAwait(false);
			if (!entries.IsSuccess || !status.IsSuccess || !capability.IsSuccess)
			{
				return PanelResults.Refuse((entries.Failure ?? status.Failure ?? capability.Failure)!);
			}

			var events = entries.Value.Select(entry => LogEvent.FromLine(entry.Line)).ToList();
			var inferred = status.Value.IsActive ? OnlinePlayers.Infer(events) : [];
			var telemetry = (await monitors.GetAsync(instance).ConfigureAwait(false)).CurrentTelemetry();

			// Телеметрия, если она свежая, иначе список игроков по журналу.
			var fresh = TelemetryNumbers.Truthy(telemetry["available"]) && !TelemetryNumbers.Truthy(telemetry["stale"]);
			var online = fresh
				? [.. (telemetry["players"] as JsonArray ?? []).OfType<JsonObject>().Select(player => (JsonObject) player.DeepClone())]
				: inferred.Select(player => new JsonObject { ["name"] = player.Name, ["since"] = player.Since }).ToList();

			var since = inferred
				.GroupBy(player => player.Name.ToLowerInvariant(), StringComparer.Ordinal)
				.ToDictionary(group => group.Key, group => group.Last().Since, StringComparer.Ordinal);
			foreach (var player in online.Where(player => !player.ContainsKey("since")))
			{
				player["since"] = since.GetValueOrDefault(TelemetryNumbers.Text(player["name"]).ToLowerInvariant(), "—");
			}

			var playerEvents = events.Where(item => item.PlayerChange is not null).TakeLast(100);

			// Наблюдатель не видит ни адреса из телеметрии, ни строку входа, в которой он есть.
			var hide = allowed.Value.HidesAddresses;
			if (hide)
			{
				foreach (var player in online)
				{
					player["ip"] = null;
					player["since"] = AddressMask.Scrub(TelemetryNumbers.Text(player["since"]), hide: true);
				}

				playerEvents = playerEvents.Select(item => item.WithLine(line => AddressMask.Scrub(line, hide: true)));
			}

			return PanelResults.Json(new PlayersResponse
			{
				Online = online,
				Events = [.. playerEvents.Select(ObservabilityEndpoints.Event)],
				Commands = new CommandCapabilityResponse
				{
					Commands = capability.Value.IsAvailable,
					Transport = ConsoleCapability.ToWire(capability.Value.Transport),
					Detail = capability.Value.UnavailableReason ?? string.Empty,
				},
				Telemetry = new TelemetryStateResponse
				{
					Available = telemetry["available"]?.DeepClone(),
					Stale = telemetry["stale"]?.DeepClone(),
					Detail = telemetry["detail"]?.DeepClone(),
					AgeSeconds = telemetry["age_seconds"]?.DeepClone(),
				},
			});
		});

		routes.MapGet("/api/telemetry", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			ServerMonitors monitors,
			PanelOptions options,
			string player = "",
			int minutes = 60) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadTelemetry), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var name = TelemetryPlayer.Create(player);
			if (!name.IsSuccess)
			{
				return PanelResults.Refuse(name.Failure!);
			}

			var monitor = await monitors.GetAsync(Instance(http)).ConfigureAwait(false);
			var window = TimeSpan.FromMinutes(Math.Clamp(minutes, 1, options.TelemetryHistoryHours * 60));
			var current = monitor.CurrentTelemetry();

			return PanelResults.Json(new TelemetryResponse
			{
				Current = allowed.Value.HidesAddresses ? WithoutAddresses(current) : current,
				History = [.. monitor.PlayerHistory(name.Value, window).Select(sample => new PingPointResponse
				{
					Ts = sample.Ts,
					PingMilliseconds = sample.PingMilliseconds,
				})],
				Interval = options.TelemetryIntervalSeconds,
			});
		});

		routes.MapGet("/api/performance", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IServerRuntime runtime,
			ServerMonitors monitors,
			PanelOptions options,
			CancellationToken ct,
			int minutes = 60) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadTelemetry), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var instance = Instance(http);
			var span = Math.Clamp(minutes, 5, options.TelemetryHistoryHours * 60);
			var report = (await monitors.GetAsync(instance).ConfigureAwait(false)).Performance(TimeSpan.FromMinutes(span));

			var entries = await runtime.LogsAsync(instance, journalWindow, ct).ConfigureAwait(false);
			if (!entries.IsSuccess)
			{
				return PanelResults.Refuse(entries.Failure!);
			}

			var hide = allowed.Value.HidesAddresses;
			var events = entries.Value
				.Select(entry => LogEvent.FromLine(entry.Line))
				.Select(item => hide ? item.WithLine(line => AddressMask.Scrub(line, hide: true)) : item)
				.ToList();

			return PanelResults.Json(new PerformanceResponse
			{
				Series = [.. report.Series.Select(point => new PerformancePointResponse
				{
					Ts = point.Ts,
					Tps = point.Tps,
					MaxGapMilliseconds = point.MaxGapMilliseconds,
					LongTicks = point.LongTicks,
				})],
				LongTicks = [.. report.LongTicks.Select(tick => new LongTickResponse { Ts = tick.At, GapMilliseconds = tick.GapMilliseconds })],
				PingSpikes = [.. report.PingSpikes.Select(spike => new PingSpikeResponse
				{
					Ts = spike.Ts,
					Player = spike.Player,
					PingMilliseconds = spike.PingMilliseconds,
				})],
				ThresholdMilliseconds = report.ThresholdMilliseconds,
				Supported = report.Supported,
				PingCriticalMilliseconds = report.PingCriticalMilliseconds,
				Overloads = [.. events.Where(item => item.Overload).Select(item => item.Line).TakeLast(50)],
				Errors = [.. events.Where(item => item.Category == LogCategory.Error).Select(item => item.Line).TakeLast(50)],
				ModErrors = [.. events
					.Where(item => item.Category == LogCategory.Error && item.Line.Contains("mod", StringComparison.OrdinalIgnoreCase))
					.Select(item => item.Line)
					.TakeLast(25)],
				PlayerEvents = [.. events.Where(item => item.PlayerChange is not null).TakeLast(50).Select(ObservabilityEndpoints.Event)],
				Minutes = span,
			});
		});

		routes.MapGet("/api/metrics", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			ServerMonitors monitors,
			Panel.Observability.Ports.IMetricsSource source,
			PanelOptions options,
			CancellationToken ct,
			int hours = 24) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadTelemetry), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var monitor = await monitors.GetAsync(Instance(http)).ConfigureAwait(false);

			// Сразу после старта панели замера ещё нет, и он снимается здесь.
			if (!monitor.HasMetrics)
			{
				await monitor.SampleMetricsAsync(source, ct).ConfigureAwait(false);
			}

			var (latest, points) = monitor.Metrics(TimeSpan.FromHours(Math.Clamp(hours, 1, options.MetricsHistoryDays * 24)));

			return PanelResults.Json(new MetricsResponse
			{
				Current = latest,
				Points = [.. points.Select(point => point.ToJson())],
				Interval = options.MetricsIntervalSeconds,
			});
		});

		routes.MapGet("/api/telemetry/stream", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			ServerMonitors monitors,
			PanelOptions options,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadTelemetry), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var monitor = await monitors.GetAsync(Instance(http)).ConfigureAwait(false);
			var hide = allowed.Value.HidesAddresses;
			var interval = TimeSpan.FromSeconds(options.TelemetryIntervalSeconds);

			await ServerSentEvents.StreamAsync(http, interval, ct, previous =>
			{
				var current = monitor.CurrentTelemetry();
				var encoded = (hide ? WithoutAddresses(current) : current).ToJsonString(PanelJson.Options);

				// Снимок отправляется, только если изменился, иначе строка поддержания связи.
				return Task.FromResult(encoded == previous ? null : encoded);
			}).ConfigureAwait(false);

			return Results.Empty;
		});
	}

	/// <summary>Снимок без адресов игроков.</summary>
	private static JsonObject WithoutAddresses(JsonObject snapshot)
	{
		var copy = (JsonObject) snapshot.DeepClone();
		foreach (var player in (copy["players"] as JsonArray ?? []).OfType<JsonObject>())
		{
			player["ip"] = null;
		}

		return copy;
	}

	private static InstanceId Instance(HttpContext http) => PanelContext.Get(http)!.Instance;
}
