using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Panel.Access.Authorization;
using Panel.Access.Domain;
using Panel.Contracts;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.Observability.Domain;
using Panel.SharedKernel;
using Panel.Web.Application;
using Panel.Web.Middleware;

namespace Panel.Web.Endpoints;

/// <summary>
/// Реестр серверов на стороне панели: перечень, выбор, переименование, «забыть», удалить.
/// </summary>
/// <remarks>
/// «Забыть» и «удалить» — разные действия: первое убирает только запись реестра, второе
/// стирает юнит и всё дерево, и только у сервера, созданного панелью.
/// </remarks>
public static class InstanceEndpoints
{
	public static void MapInstances(this IEndpointRouteBuilder routes)
	{
		routes.MapGet("/api/instances", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IRegistryAdministration registry,
			ServerMonitors monitors,
			GetServerStatusHandler status,
			MaintenanceCoordinator coordinator,
			PanelOptions options,
			CancellationToken ct) =>
		{
			// Перечень доступен не только администратору: оператор и наблюдатель должны видеть, какой
			// сервер выбран, и переключаться на другой.
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadInstances), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var panelInstance = InstanceId.Parse(options.DefaultInstance);
			var listing = await registry.ListAsync(ct).ConfigureAwait(false);
			if (!listing.IsSuccess)
			{
				// Панель должна отвечать и без реестра: пустой перечень и причина.
				return PanelResults.Json(new InstancesResponse
				{
					Instances = [],
					Current = PanelContext.Get(http)!.Instance.Value,
					Default = panelInstance.Value,
					PanelInstance = panelInstance.Value,
					Writable = false,
					Detail = listing.Failure!.Text,
				});
			}

			var items = new List<JsonObject>();
			foreach (var entry in listing.Value.Instances.OrderBy(item => item.Id, StringComparer.Ordinal))
			{
				var item = JsonSerializer.SerializeToNode(entry, PanelJson.Options)!.AsObject();
				if (!entry.Valid || !InstanceId.Create(entry.Id).IsSuccess)
				{
					item["busy"] = false;
					item["players"] = null;
					items.Add(item);
					continue;
				}

				await AddSummaryAsync(item, InstanceId.Parse(entry.Id), monitors, status, coordinator, ct).ConfigureAwait(false);
				items.Add(item);
			}

			var registered = listing.Value.Instances.Where(item => item.Valid).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
			var effectiveDefault = listing.Value.Default is { } declared && registered.Contains(declared)
				? declared
				: panelInstance.Value;

			return PanelResults.Json(new InstancesResponse
			{
				Instances = items,
				Current = PanelContext.Get(http)!.Instance.Value,
				Default = effectiveDefault,
				PanelInstance = panelInstance.Value,
				Writable = listing.Value.Writable,
				Detail = null,
			});
		});

		routes.MapPost("/api/instances/select", async (
			HttpContext http,
			InstanceSelectRequest body,
			CommandAuthorizer authorizer,
			IRegistryStore registry,
			PanelOptions options,
			CancellationToken ct) =>
		{
			// Переключение на другой сервер ничего не меняет, наблюдателю оно разрешено.
			var allowed = PanelResults.Authorize(http, authorizer, typeof(SelectInstance));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var instance = InstanceId.Create(body.Instance);
			var snapshot = await registry.LoadAsync(ct).ConfigureAwait(false);
			if (!instance.IsSuccess || snapshot is not RegistrySnapshot.Available available || available.Find(instance.Value) is null)
			{
				return PanelResults.Refuse(StatusCodes.Status404NotFound, "Такой сервер не зарегистрирован");
			}

			// Cookie нужна потому, что EventSource не отправляет заголовков, и без неё живые потоки
			// показывали бы один сервер под именем другого.
			http.Response.Cookies.Append(PanelContextMiddleware.InstanceCookie, instance.Value.Value, new CookieOptions
			{
				HttpOnly = false,
				Secure = options.SecureCookie,
				SameSite = SameSiteMode.Strict,
				MaxAge = options.SessionLifetime,
				Path = "/",
			});

			return PanelResults.Json(new JsonObject { ["ok"] = true, ["instance"] = instance.Value.Value });
		});

		routes.MapPost("/api/instances/rename", async (
			HttpContext http,
			InstanceRenameRequest body,
			CommandAuthorizer authorizer,
			IRegistryAdministration registry,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ManageInstances));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var instance = InstanceId.Create(body.Instance);
			var name = InstanceName.Create(body.Name);
			if (!instance.IsSuccess || !name.IsSuccess)
			{
				return PanelResults.Refuse((instance.Failure ?? name.Failure)!);
			}

			var renamed = await registry.RenameAsync(instance.Value, name.Value, ct).ConfigureAwait(false);
			if (renamed.IsSuccess)
			{
				await audit.RecordAsync(http, "instance_renamed", $"{body.Instance} -> {name.Value.Value}", ct).ConfigureAwait(false);
			}

			return PanelResults.From(renamed);
		});

		routes.MapPost("/api/instances/forget", async (
			HttpContext http,
			InstanceConfirmedRequest body,
			CommandAuthorizer authorizer,
			IRegistryAdministration registry,
			MaintenanceCoordinator coordinator,
			PanelOptions options,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ManageInstances));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var instance = InstanceId.Create(body.Instance);
			if (!instance.IsSuccess)
			{
				return PanelResults.Refuse(instance.Failure!);
			}

			var confirmed = Confirmation.Exactly("ЗАБЫТЬ " + body.Instance, "FORGET " + body.Instance).Check(body.Confirmation);
			if (!confirmed.IsSuccess)
			{
				return PanelResults.Refuse(confirmed.Failure!);
			}

			// Сервер PANEL_INSTANCE панель использует и без реестра: если его убрать, сэмплеры будут
			// обращаться к демону по имени, которого он уже не знает.
			if (instance.Value.Value == options.DefaultInstance)
			{
				return PanelResults.Refuse(StatusCodes.Status400BadRequest,
					"Это сервер, выбранный панелью по умолчанию (PANEL_INSTANCE). " +
					"Его нельзя забыть: панель продолжит обращаться к нему и без реестра. " +
					"Смените PANEL_INSTANCE в настройках панели и перезапустите её.");
			}

			// Занятость проверяет сам маршрут: речь о забываемом сервере, а не о выбранном.
			if (coordinator.IsBusy(instance.Value))
			{
				return PanelResults.Refuse(StatusCodes.Status409Conflict, InstanceRemoval.BusyRefusal);
			}

			var forgotten = await registry.ForgetAsync(instance.Value, ct).ConfigureAwait(false);
			if (forgotten.IsSuccess)
			{
				await audit.RecordAsync(http, "instance_forgotten", body.Instance, ct).ConfigureAwait(false);
			}

			return PanelResults.From(forgotten);
		});

		routes.MapPost("/api/instances/delete", async (
			HttpContext http,
			InstanceConfirmedRequest body,
			CommandAuthorizer authorizer,
			InstanceRemoval removal,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ManageInstances));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var instance = InstanceId.Create(body.Instance);
			if (!instance.IsSuccess)
			{
				return PanelResults.Refuse(instance.Failure!);
			}

			var deleted = await removal.DeleteAsync(instance.Value, body.Confirmation, ct).ConfigureAwait(false);
			if (deleted.IsSuccess)
			{
				await audit.RecordAsync(http, "instance_deleted", body.Instance, ct).ConfigureAwait(false);
			}

			return PanelResults.From(deleted);
		});
	}

	/// <summary>
	/// Что панель уже знает о сервере: вердикт готовности, игроки, свежесть телеметрии и
	/// последний замер метрик.
	/// </summary>
	private static async Task AddSummaryAsync(
		JsonObject item,
		InstanceId instance,
		ServerMonitors monitors,
		GetServerStatusHandler status,
		MaintenanceCoordinator coordinator,
		CancellationToken ct)
	{
		var monitor = await monitors.GetAsync(instance).ConfigureAwait(false);
		var telemetry = monitor.CurrentTelemetry();
		var (metrics, _) = monitor.Metrics(TimeSpan.Zero);
		var health = await status.HandleAsync(new GetServerStatus(instance), ct).ConfigureAwait(false);

		item["watchdog"] = health.IsSuccess ? JsonSerializer.SerializeToNode(health.Value.Health, PanelJson.Options) : null;
		item["players"] = TelemetryNumbers.Truthy(telemetry["available"])
			? (telemetry["players"] as JsonArray)?.Count ?? 0
			: null;
		item["telemetry_stale"] = TelemetryNumbers.Truthy(telemetry["stale"]);
		item["world_size"] = metrics?["world_size"]?.DeepClone();
		item["disk_free"] = metrics?["disk_free"]?.DeepClone();
		item["last_backup"] = metrics?["last_backup"]?.DeepClone();

		// Сервер занят, если на него есть аренда; обновления берут ту же аренду.
		item["busy"] = coordinator.IsBusy(instance);
	}
}

/// <summary>Ответ <c>GET /api/instances</c>.</summary>
public sealed record InstancesResponse
{
	[JsonPropertyName("instances")] public required IReadOnlyList<JsonObject> Instances { get; init; }
	[JsonPropertyName("current")] public required string Current { get; init; }
	[JsonPropertyName("default")] public required string Default { get; init; }

	/// <summary><c>PANEL_INSTANCE</c>: сервер, который нельзя ни «забыть», ни удалить.</summary>
	[JsonPropertyName("panel_instance")] public required string PanelInstance { get; init; }

	[JsonPropertyName("writable")] public required bool Writable { get; init; }
	/// <summary>Почему перечень пуст: реестр недоступен. <c>null</c>, если реестр прочитан.</summary>
	[JsonPropertyName("detail")] public required Phrase? Detail { get; init; }
}

public sealed record InstanceSelectRequest
{
	[JsonPropertyName("instance")] public string Instance { get; init; } = string.Empty;
}

public sealed record InstanceRenameRequest
{
	[JsonPropertyName("instance")] public string Instance { get; init; } = string.Empty;
	[JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
}

public sealed record InstanceConfirmedRequest
{
	[JsonPropertyName("instance")] public string Instance { get; init; } = string.Empty;
	[JsonPropertyName("confirmation")] public string Confirmation { get; init; } = string.Empty;
}
