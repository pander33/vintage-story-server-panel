using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Panel.Access.Authorization;
using Panel.Maintenance.Ports;
using Panel.SharedKernel;
using Panel.Web.Application;
using Panel.Web.Middleware;

namespace Panel.Web.Endpoints;

/// <summary>
/// Пакетное обновление модов, обновление программы сервера и откат: вкладка «Обслуживание».
/// </summary>
/// <remarks>
/// Запуск отвечает 202 и идентификатором операции: операция идёт в фоне, её ход виден в
/// <c>GET /api/updates</c>. Массового обновления без подтверждения нет (INV-28): набор модов
/// человек собирает из найденных проверкой, и каждый набор подтверждается фразой.
/// </remarks>
public static partial class UpdateEndpoints
{
	/// <summary>Предел размера пакета сервера, 4 ГБ.</summary>
	private const int gamePackageMegabytes = 4096;

	public static void MapUpdates(this IEndpointRouteBuilder routes)
	{
		routes.MapGet("/api/updates", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			UpdateService updates,
			IGamePackages packages,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadUpdates), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var history = await packages.HistoryAsync(Instance(http), ct).ConfigureAwait(false);
			if (!history.IsSuccess)
			{
				return PanelResults.Refuse(history.Failure!);
			}

			var snapshot = await updates.SnapshotAsync(Instance(http), ct).ConfigureAwait(false);
			snapshot["game_history"] = System.Text.Json.JsonSerializer.SerializeToNode(
				history.Value.Backups, Panel.Contracts.PanelJson.Options);
			return PanelResults.Json(snapshot);
		});

		routes.MapGet("/api/updates/game/plan", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			UpdateService updates,
			CancellationToken ct,
			string? version = null) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadUpdates), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			if (!TargetVersion().IsMatch(version ?? string.Empty))
			{
				return PanelResults.Refuse(Failure.Invalid("Укажите корректную версию"));
			}

			return PanelResults.From(await updates.GamePlanAsync(Instance(http), version!, ct).ConfigureAwait(false));
		});

		routes.MapPost("/api/updates/mods", async (
			HttpContext http,
			ModBatchRequest body,
			CommandAuthorizer authorizer,
			UpdateService updates,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeUpdates));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			// Границы ModBatchBody: от 1 до 40 модов, готовность от минуты до часа.
			if (body.Items is not { Count: >= 1 and <= 40 } || body.ReadinessTimeout is < 60 or > 3600)
			{
				return PanelResults.Refuse(Failure.Invalid("Выберите от одного до 40 обновлений модов и срок готовности от 60 до 3600 секунд"));
			}

			var confirmed = Confirmation.Exactly("ОБНОВИТЬ МОДЫ", "UPDATE MODS").Check(body.Confirmation);
			if (!confirmed.IsSuccess)
			{
				return PanelResults.Refuse(confirmed.Failure!);
			}

			var launched = await updates
				.LaunchModsAsync(Instance(http), [.. body.Items.Select(item => new ModUpdateItem(item.Identifier, item.ReleaseId))], body.ReadinessTimeout)
				.ConfigureAwait(false);
			if (!launched.IsSuccess)
			{
				return PanelResults.Refuse(StatusCodes.Status409Conflict, launched.Failure!.Text);
			}

			await audit.RecordAsync(http, "mod_batch_update_queued", launched.Value, ct).ConfigureAwait(false);
			return Queued(launched.Value);
		});

		routes.MapPost("/api/updates/game", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			UpdateService updates,
			StagingWriter staging,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeUpdates));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var form = await UploadLimits.ReadFormAsync(http, gamePackageMegabytes, ct).ConfigureAwait(false);
			if (form.TooLarge)
			{
				return PanelResults.Refuse(Failure.TooLarge("Архив сервера больше 4 ГБ"));
			}

			var fields = form.Form;
			var confirmed = Confirmation.Exactly("ОБНОВИТЬ СЕРВЕР", "UPDATE SERVER").Check(fields?["confirmation"].ToString());
			if (!confirmed.IsSuccess)
			{
				return PanelResults.Refuse(confirmed.Failure!);
			}

			var target = fields?["target_version"].ToString() ?? string.Empty;
			if (!TargetVersion().IsMatch(target))
			{
				return PanelResults.Refuse(Failure.Invalid("Укажите версию пакета, например 1.22.8"));
			}

			var file = fields?.Files.GetFile("file");
			// Сервер для Linux поставляется в .tar.gz, для Windows — в .zip. Формат определяется по
			// имени файла, а годность пакета демон проверяет по содержимому.
			var zipped = file?.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true;
			if (file is null || !(zipped
								  || file.FileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
								  || file.FileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)))
			{
				return PanelResults.Refuse(Failure.Invalid("Нужен официальный архив сервера: .tar.gz для Linux или .zip для Windows"));
			}

			// План проверяется до записи файла, чтобы негодная версия не стоила четырёх гигабайт на диске.
			var plan = await updates.GamePlanAsync(Instance(http), target, ct).ConfigureAwait(false);
			if (!plan.IsSuccess)
			{
				return PanelResults.Refuse(plan.Failure!);
			}

			if (plan.Value["blockers"] is JsonArray { Count: > 0 })
			{
				return PanelResults.Refuse(Failure.Conflict("Установлены моды, которые не заявляют совместимость с этой версией"));
			}

			var risky = plan.Value["warnings"] is JsonArray { Count: > 0 } || plan.Value["branch_change"]?.GetValue<bool>() == true;
			if (risky && !FormFlag(fields?["allow_unverified"].ToString()))
			{
				return PanelResults.Refuse(Failure.Conflict("План содержит непроверенные моды или смену ветки; подтвердите риск"));
			}

			var staged = StagingWriter.Reserve(StagedFile.Purpose.Game, zipped ? ".zip" : ".tar.gz");
			var queued = false;
			try
			{
				await using (var stream = file.OpenReadStream())
				{
					var written = await staging
						.WriteAsync(stream, staged, gamePackageMegabytes, ct, Phrase.From("Архив сервера больше 4 ГБ"))
						.ConfigureAwait(false);
					if (!written.IsSuccess)
					{
						return PanelResults.Refuse(written.Failure!);
					}
				}

				var launched = await updates.LaunchGameAsync(Instance(http), staged, target).ConfigureAwait(false);
				if (!launched.IsSuccess)
				{
					return PanelResults.Refuse(StatusCodes.Status409Conflict, launched.Failure!.Text);
				}

				queued = true;
				await audit.RecordAsync(http, "game_update_queued", launched.Value, ct).ConfigureAwait(false);
				return Queued(launched.Value);
			}
			finally
			{
				// Запущенная операция сама удалит пакет, когда он станет не нужен.
				if (!queued)
				{
					staging.Discard(staged);
				}
			}
		});

		routes.MapPost("/api/updates/game/rollback", async (
			HttpContext http,
			GameRollbackRequest body,
			CommandAuthorizer authorizer,
			UpdateService updates,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeUpdates));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var confirmed = Confirmation.Exactly("ОТКАТИТЬ " + body.Name, "ROLL BACK " + body.Name).Check(body.Confirmation);
			if (!confirmed.IsSuccess)
			{
				return PanelResults.Refuse(confirmed.Failure!);
			}

			var launched = await updates.LaunchRollbackAsync(Instance(http), body.Name).ConfigureAwait(false);
			if (!launched.IsSuccess)
			{
				return PanelResults.Refuse(StatusCodes.Status409Conflict, launched.Failure!.Text);
			}

			await audit.RecordAsync(http, "game_update_rollback_queued", body.Name, ct).ConfigureAwait(false);
			return Queued(launched.Value);
		});
	}

	private static IResult Queued(string runId) =>
		PanelResults.Json(new JsonObject { ["status"] = "queued", ["run_id"] = runId }, StatusCodes.Status202Accepted);

	/// <summary>Логическое поле формы по правилам FastAPI: 1, true, on, yes в любом регистре.</summary>
	private static bool FormFlag(string? value) =>
		value?.Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes" or "y" or "t";

	private static InstanceId Instance(HttpContext http) => PanelContext.Get(http)!.Instance;

	/// <summary>Версия пакета в маршрутах: шире <c>GAME_VERSION_RE</c> демона, суффикс длиннее и с дефисами.</summary>
	[GeneratedRegex(@"\A\d{1,3}\.\d{1,3}\.\d{1,4}(?:-[A-Za-z0-9.-]+)?\z")]
	private static partial Regex TargetVersion();
}

/// <summary>Элемент <c>items</c>.</summary>
public sealed record ModBatchItem
{
	[JsonPropertyName("identifier")] public required string Identifier { get; init; }

	[JsonPropertyName("release_id")] public required long ReleaseId { get; init; }
}

/// <summary>Тело <c>POST /api/updates/mods</c>.</summary>
public sealed record ModBatchRequest
{
	[JsonPropertyName("items")] public required IReadOnlyList<ModBatchItem> Items { get; init; }

	[JsonPropertyName("readiness_timeout")] public int ReadinessTimeout { get; init; } = 900;

	[JsonPropertyName("confirmation")] public required string Confirmation { get; init; }
}

/// <summary>Тело <c>POST /api/updates/game/rollback</c>.</summary>
public sealed record GameRollbackRequest
{
	[JsonPropertyName("name")] public required string Name { get; init; }

	[JsonPropertyName("confirmation")] public required string Confirmation { get; init; }
}
