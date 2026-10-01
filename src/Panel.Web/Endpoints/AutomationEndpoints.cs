using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Panel.Access.Authorization;
using Panel.Maintenance.Domain;
using Panel.SharedKernel;
using Panel.Web.Application;
using Panel.Web.Middleware;

namespace Panel.Web.Endpoints;

/// <summary>Плановое обслуживание и наблюдатель готовности: вкладка «Обслуживание».</summary>
public static class AutomationEndpoints
{
	public static void MapAutomation(this IEndpointRouteBuilder routes)
	{
		// Чтение показывает последний вердикт: цепочку и автоматическое восстановление ведёт такт.
		routes.MapGet("/api/automation", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			AutomationService automation,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadAutomation), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			return PanelResults.Json(await automation.SnapshotAsync(Instance(http), ct).ConfigureAwait(false));
		});

		routes.MapPost("/api/automation/schedules", async (
			HttpContext http,
			JsonObject body,
			CommandAuthorizer authorizer,
			AutomationService automation,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeAutomation));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var created = await automation
				.AddJobAsync(Instance(http), MaintenanceJob.Draft(body), ct)
				.ConfigureAwait(false);
			if (!created.IsSuccess)
			{
				return PanelResults.Refuse(created.Failure!);
			}

			await audit.RecordAsync(http, "schedule_created", TelemetryText(created.Value["id"]), ct).ConfigureAwait(false);
			return PanelResults.Json(created.Value);
		});

		// При изменении приходит вся форма: её поля с умолчаниями формы накладываются на задание.
		routes.MapPut("/api/automation/schedules/{job_id}", async (
			HttpContext http,
			string job_id,
			JsonObject body,
			CommandAuthorizer authorizer,
			AutomationService automation,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeAutomation));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var draft = MaintenanceJob.Draft(body);
			var updated = await automation
				.UpdateJobAsync(Instance(http), job_id, _ => draft, ct)
				.ConfigureAwait(false);
			if (!updated.IsSuccess)
			{
				return PanelResults.Refuse(updated.Failure!);
			}

			await audit.RecordAsync(http, "schedule_updated", job_id, ct).ConfigureAwait(false);
			return PanelResults.Json(updated.Value);
		});

		routes.MapPost("/api/automation/schedules/{job_id}/toggle", async (
			HttpContext http,
			string job_id,
			ToggleRequest body,
			CommandAuthorizer authorizer,
			AutomationService automation,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeAutomation));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var updated = await automation
				.UpdateJobAsync(Instance(http), job_id, current => current with { Enabled = body.Enabled }, ct)
				.ConfigureAwait(false);
			if (!updated.IsSuccess)
			{
				return PanelResults.Refuse(updated.Failure!);
			}

			await audit.RecordAsync(http, "schedule_toggled", $"{job_id}:{PythonBool(body.Enabled)}", ct).ConfigureAwait(false);
			return PanelResults.Json(updated.Value);
		});

		routes.MapDelete("/api/automation/schedules/{job_id}", async (
			HttpContext http,
			string job_id,
			CommandAuthorizer authorizer,
			AutomationService automation,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeAutomation));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var deleted = await automation.DeleteJobAsync(Instance(http), job_id, ct).ConfigureAwait(false);
			if (!deleted.IsSuccess)
			{
				return PanelResults.Refuse(deleted.Failure!);
			}

			await audit.RecordAsync(http, "schedule_deleted", job_id, ct).ConfigureAwait(false);
			return PanelResults.Json(new JsonObject { ["ok"] = true });
		});

		routes.MapPost("/api/automation/schedules/{job_id}/run", async (
			HttpContext http,
			string job_id,
			CommandAuthorizer authorizer,
			AutomationService automation,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeAutomation));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var launched = await automation.LaunchAsync(Instance(http), job_id).ConfigureAwait(false);
			if (!launched.IsSuccess)
			{
				return PanelResults.Refuse(launched.Failure!);
			}

			await audit.RecordAsync(http, "schedule_started", job_id, ct).ConfigureAwait(false);
			return PanelResults.Json(new JsonObject { ["ok"] = true, ["run_id"] = launched.Value });
		});

		// Объявления по расписанию: текст игрокам раз в N минут.
		routes.MapPost("/api/automation/announcements", async (
			HttpContext http,
			JsonObject body,
			CommandAuthorizer authorizer,
			AutomationService automation,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeAutomation));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var created = await automation
				.AddAnnouncementAsync(Instance(http), ScheduledAnnouncement.Draft(body), ct)
				.ConfigureAwait(false);
			if (!created.IsSuccess)
			{
				return PanelResults.Refuse(created.Failure!);
			}

			await audit.RecordAsync(http, "announcement_created", TelemetryText(created.Value["id"]), ct).ConfigureAwait(false);
			return PanelResults.Json(created.Value);
		});

		routes.MapPut("/api/automation/announcements/{announcement_id}", async (
			HttpContext http,
			string announcement_id,
			JsonObject body,
			CommandAuthorizer authorizer,
			AutomationService automation,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeAutomation));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var draft = ScheduledAnnouncement.Draft(body);
			var updated = await automation
				.UpdateAnnouncementAsync(Instance(http), announcement_id, _ => draft, ct)
				.ConfigureAwait(false);
			if (!updated.IsSuccess)
			{
				return PanelResults.Refuse(updated.Failure!);
			}

			await audit.RecordAsync(http, "announcement_updated", announcement_id, ct).ConfigureAwait(false);
			return PanelResults.Json(updated.Value);
		});

		routes.MapPost("/api/automation/announcements/{announcement_id}/toggle", async (
			HttpContext http,
			string announcement_id,
			ToggleRequest body,
			CommandAuthorizer authorizer,
			AutomationService automation,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeAutomation));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var updated = await automation
				.UpdateAnnouncementAsync(Instance(http), announcement_id, current => current with { Enabled = body.Enabled }, ct)
				.ConfigureAwait(false);
			if (!updated.IsSuccess)
			{
				return PanelResults.Refuse(updated.Failure!);
			}

			await audit.RecordAsync(http, "announcement_toggled", $"{announcement_id}:{PythonBool(body.Enabled)}", ct).ConfigureAwait(false);
			return PanelResults.Json(updated.Value);
		});

		routes.MapDelete("/api/automation/announcements/{announcement_id}", async (
			HttpContext http,
			string announcement_id,
			CommandAuthorizer authorizer,
			AutomationService automation,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeAutomation));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var deleted = await automation.DeleteAnnouncementAsync(Instance(http), announcement_id, ct).ConfigureAwait(false);
			if (!deleted.IsSuccess)
			{
				return PanelResults.Refuse(deleted.Failure!);
			}

			await audit.RecordAsync(http, "announcement_deleted", announcement_id, ct).ConfigureAwait(false);
			return PanelResults.Json(new JsonObject { ["ok"] = true });
		});

		routes.MapPut("/api/automation/watchdog", async (
			HttpContext http,
			JsonObject body,
			CommandAuthorizer authorizer,
			AutomationService automation,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeAutomation));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var saved = await automation.SetWatchdogAsync(Instance(http), body, ct).ConfigureAwait(false);
			if (!saved.IsSuccess)
			{
				return PanelResults.Refuse(saved.Failure!);
			}

			var detail = $"enabled={PythonBool(saved.Value["enabled"]?.GetValue<bool>() ?? false)}"
				+ $",auto_restart={PythonBool(saved.Value["auto_restart"]?.GetValue<bool>() ?? false)}";
			await audit.RecordAsync(http, "watchdog_updated", detail, ct).ConfigureAwait(false);
			return PanelResults.Json(saved.Value);
		});

		routes.MapPost("/api/automation/watchdog/check", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			AutomationService automation,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeAutomation));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var health = await automation.CheckWatchdogAsync(Instance(http), ct).ConfigureAwait(false);
			var state = TelemetryText(health["state"]);
			await audit.RecordAsync(http, "watchdog_checked", state.Length > 0 ? state : "unknown", ct).ConfigureAwait(false);
			return PanelResults.Json(health);
		});
	}

	/// <summary>Логическое значение в строке аудита: True или False.</summary>
	private static string PythonBool(bool value) => value ? "True" : "False";

	private static string TelemetryText(JsonNode? node) => Observability.Domain.TelemetryNumbers.Text(node);

	private static InstanceId Instance(HttpContext http) => PanelContext.Get(http)!.Instance;
}

/// <summary>Тело <c>POST …/toggle</c>.</summary>
public sealed record ToggleRequest
{
	[JsonPropertyName("enabled")] public required bool Enabled { get; init; }
}
