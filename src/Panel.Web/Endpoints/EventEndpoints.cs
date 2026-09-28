using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Panel.Access.Authorization;
using Panel.Contracts;
using Panel.Observability.Domain;
using Panel.Observability.Infrastructure;
using Panel.SharedKernel;
using Panel.Web.Application;
using Panel.Web.Middleware;

namespace Panel.Web.Endpoints;

/// <summary>Лента событий сервера и уведомления панели.</summary>
public static class EventEndpoints
{
	public static void MapEvents(this IEndpointRouteBuilder routes)
	{
		routes.MapGet("/api/events", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			EventBus events,
			CancellationToken ct,
			int limit = 100,
			string severity = "all",
			string search = "") =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadEvents), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var filter = EventFilter.Create(severity, search);
			if (!filter.IsSuccess)
			{
				return PanelResults.Refuse(filter.Failure!);
			}

			var recent = await events
				.RecentAsync(Instance(http), filter.Value, Math.Clamp(limit, 1, 500), ct)
				.ConfigureAwait(false);

			return PanelResults.Json(new EventsResponse
			{
				Events = recent,
				Notifications = events.Settings.Current.Public(),
			});
		});

		routes.MapGet("/api/events/stream", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			EventBus events,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadEvents), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var instance = Instance(http);
			var everything = EventFilter.Create("all", string.Empty).Value;

			// Если что-то изменилось, последние 50 событий целиком.
			await ServerSentEvents.StreamAsync(http, TimeSpan.FromSeconds(3), ct, async previous =>
			{
				var recent = await events.RecentAsync(instance, everything, 50, ct).ConfigureAwait(false);
				var encoded = LocalizedJson.Serialize(new JsonArray([.. recent]), http);
				return encoded == previous ? null : encoded;
			}).ConfigureAwait(false);

			return Results.Empty;
		});

		routes.MapPut("/api/notifications", async (
			HttpContext http,
			NotificationRequest body,
			CommandAuthorizer authorizer,
			EventBus events,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ConfigureNotifications));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var updated = events.Settings.Current.Apply(new NotificationUpdate(
				body.Enabled,
				body.MinimumSeverity,
				body.CooldownSeconds,
				body.WebhookUrl,
				body.DiscordUrl,
				body.TelegramToken,
				body.TelegramChatId,
				PhraseCatalog.Code(LocalizedJson.LanguageOf(http))));
			if (!updated.IsSuccess)
			{
				return PanelResults.Refuse(updated.Failure!);
			}

			var saved = await events.Settings.SaveAsync(updated.Value, ct).ConfigureAwait(false);
			if (!saved.IsSuccess)
			{
				return PanelResults.Refuse(saved.Failure!);
			}

			await audit.RecordAsync(http, "notifications_updated", string.Empty, ct).ConfigureAwait(false);
			return PanelResults.Json(events.Settings.Current.Public());
		});

		routes.MapPost("/api/notifications/test", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			EventBus events,
			NotificationSender sender,
			TimeProvider clock,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ConfigureNotifications));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			// Отказ канала — 502: это ответ внешнего сервиса.
			var sent = await sender
				.SendAsync(NotificationMessage.Test(clock.GetUtcNow()), events.Settings.Current, PhraseCatalog.Renderer(LocalizedJson.LanguageOf(http)), ct)
				.ConfigureAwait(false);
			if (!sent.IsSuccess)
			{
				return PanelResults.Refuse(StatusCodes.Status502BadGateway, sent.Failure!.Text);
			}

			await audit.RecordAsync(http, "notification_test", string.Empty, ct).ConfigureAwait(false);
			return PanelResults.Json(new JsonObject { ["ok"] = true });
		});
	}

	private static InstanceId Instance(HttpContext http) => PanelContext.Get(http)!.Instance;
}

/// <summary>Ответ <c>GET /api/events</c>: записи ленты как есть и открытая часть настроек.</summary>
public sealed record EventsResponse
{
	[JsonPropertyName("events")] public required IReadOnlyList<JsonObject> Events { get; init; }
	[JsonPropertyName("notifications")] public required JsonObject Notifications { get; init; }
}

/// <summary>
/// Тело <c>PUT /api/notifications</c>. Секрет <c>null</c> означает «оставить как есть».
/// </summary>
public sealed record NotificationRequest
{
	[JsonPropertyName("enabled")] public bool Enabled { get; init; }
	[JsonPropertyName("minimum_severity")] public string MinimumSeverity { get; init; } = "warning";
	[JsonPropertyName("cooldown_seconds")] public int CooldownSeconds { get; init; } = 300;
	[JsonPropertyName("webhook_url")] public string? WebhookUrl { get; init; }
	[JsonPropertyName("discord_url")] public string? DiscordUrl { get; init; }
	[JsonPropertyName("telegram_token")] public string? TelegramToken { get; init; }
	[JsonPropertyName("telegram_chat_id")] public string? TelegramChatId { get; init; }
}
