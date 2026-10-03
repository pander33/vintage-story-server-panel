using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Panel.Access.Authorization;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.Observability.Domain;
using Panel.Runtime.Domain;
using Panel.SharedKernel;
using Panel.Web.Application;

namespace Panel.Web.Endpoints;

/// <summary>
/// <c>GET /metrics</c> для Prometheus (findings §82): последний замер каждого сервера реестра и
/// вердикт готовности. Ни демон, ни игра здесь не опрашиваются: числа берутся из того, что уже
/// собрал <see cref="MonitoringService"/>, поэтому частый опрос Prometheus не добавляет нагрузки.
/// </summary>
/// <remarks>
/// Доступ — API-токен (<c>Authorization: Bearer</c>) или сессия с правом наблюдателя; токена
/// с ролью «наблюдатель» Prometheus достаточно.
/// </remarks>
public static class MetricsExportEndpoint
{
	public static void MapMetricsExport(this IEndpointRouteBuilder routes)
	{
		routes.MapGet("/metrics", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IRegistryStore registry,
			ServerMonitors monitors,
			AutomationService automation,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ExportMetrics), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var snapshot = await registry.LoadAsync(ct).ConfigureAwait(false);
			if (snapshot is RegistrySnapshot.Unavailable unavailable)
			{
				return PanelResults.Refuse(Failure.Unavailable(unavailable.Detail));
			}

			var servers = snapshot is RegistrySnapshot.Available available ? available.Servers : [];
			var samples = new List<ServerMetricsSample>(servers.Count);
			foreach (var server in servers)
			{
				var monitor = await monitors.GetAsync(server.Id).ConfigureAwait(false);
				var (latest, _) = monitor.Metrics(TimeSpan.Zero);
				var (verdict, _) = await automation.CurrentHealthAsync(server.Id).ConfigureAwait(false);
				samples.Add(ServerMetricsSample.FromLatest(server.Id, latest, (verdict?.State ?? HealthState.Unknown).ToWire()));
			}

			return Results.Text(new MetricsExposition(AgentProtocol.Build, samples).ToText(), MetricsExposition.ContentType);
		});
	}
}
