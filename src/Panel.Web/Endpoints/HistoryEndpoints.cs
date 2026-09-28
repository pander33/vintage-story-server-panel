using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Panel.Access.Authorization;
using Panel.Content.Domain;
using Panel.Content.Ports;
using Panel.Contracts.Http;
using Panel.Observability.Domain;
using Panel.SharedKernel;
using Panel.Web.Application;
using Panel.Web.Middleware;

namespace Panel.Web.Endpoints;

/// <summary>
/// История «на всякий случай»: три маршрута над тремя хранилищами.
/// </summary>
public static class HistoryEndpoints
{
	public static void MapHistory(this IEndpointRouteBuilder routes)
	{
		routes.MapGet("/api/history", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IHistoryStore history,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadHistory), requireCsrf: false);

			return allowed.IsSuccess
				? PanelResults.From(await history.ListAsync(Instance(http), ct).ConfigureAwait(false))
				: PanelResults.Refuse(allowed.Failure!);
		});

		routes.MapPost("/api/history/delete", async (
			HttpContext http,
			HistoryDeleteRequest body,
			CommandAuthorizer authorizer,
			IHistoryStore history,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeHistory));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var store = HistoryStoreWire.Parse(body.Store);
			var key = HistoryKey.Create(body.Key);
			if (!store.IsSuccess || !key.IsSuccess)
			{
				return PanelResults.Refuse((store.Failure ?? key.Failure)!);
			}

			var deleted = await history.DeleteAsync(Instance(http), store.Value, key.Value, ct).ConfigureAwait(false);
			if (deleted.IsSuccess)
			{
				await audit.RecordAsync(http, "history_deleted", $"{body.Store}:{body.Key}", ct).ConfigureAwait(false);
			}

			return PanelResults.From(deleted);
		});

		routes.MapPost("/api/history/prune", async (
			HttpContext http,
			HistoryPruneRequest body,
			CommandAuthorizer authorizer,
			IHistoryStore history,
			AuditLog audit,
			EventBus events,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeHistory));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var keep = HistoryKeep.Create(body.Keep);
			if (!keep.IsSuccess)
			{
				return PanelResults.Refuse(keep.Failure!);
			}

			// Фраза проверяется до обращения к демону.
			var confirmed = Confirmation.Exactly("ОЧИСТИТЬ ИСТОРИЮ", "CLEAR HISTORY").Check(body.Confirmation);
			if (!confirmed.IsSuccess)
			{
				return PanelResults.Refuse(confirmed.Failure!);
			}

			var store = HistoryStoreWire.Parse(body.Store);
			if (!store.IsSuccess)
			{
				return PanelResults.Refuse(store.Failure!);
			}

			var pruned = await history.PruneAsync(Instance(http), store.Value, keep.Value, ct).ConfigureAwait(false);
			if (!pruned.IsSuccess)
			{
				return PanelResults.Refuse(pruned.Failure!);
			}

			var removed = pruned.Value.Removed.Count;
			await audit
				.RecordAsync(http, "history_pruned", $"{body.Store}:keep={keep.Value.Count}:removed={removed}", ct)
				.ConfigureAwait(false);

			if (removed > 0)
			{
				await events.EmitAsync(
						Instance(http),
						"history.pruned",
						EventSeverity.Info,
						Phrase.From("История очищена"),
						Phrase.Format($"{body.Store}: удалено {removed}"),
						"recovery",
						notify: false,
						ct: ct)
					.ConfigureAwait(false);
			}

			return PanelResults.Json(pruned.Value);
		});
	}

	private static InstanceId Instance(HttpContext http) => PanelContext.Get(http)!.Instance;
}
