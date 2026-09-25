using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Panel.Access.Authorization;
using Panel.Catalogues.Infrastructure;
using Panel.Content.Ports;
using Panel.SharedKernel;
using Panel.Web.Application;
using Panel.Web.Middleware;

namespace Panel.Web.Endpoints;

/// <summary>Каталог версий игры и ModDB: поиск модов, релизы, проверка обновлений.</summary>
/// <remarks>
/// Отказ внешнего сервиса — 502: это ответ чужого API, а не ошибка панели или недоступный
/// демон (503).
/// </remarks>
public static class CatalogueEndpoints
{
	/// <summary>Сколько модов проверяется на обновления за раз, как <c>installed[:40]</c>.</summary>
	private const int updateCheckLimit = 40;

	public static void MapCatalogue(this IEndpointRouteBuilder routes)
	{
		routes.MapGet("/api/catalogue", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			VersionCatalogue catalogue,
			CancellationToken ct,
			string? refresh = null) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadCatalogue), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var snapshot = await catalogue.SnapshotAsync(QueryFlag(refresh), ct).ConfigureAwait(false);
			return snapshot.IsSuccess ? PanelResults.Json(snapshot.Value.ToJson()) : Upstream(snapshot.Failure!);
		});

		routes.MapGet("/api/moddb/search", async (
			HttpContext http,
			string q,
			CommandAuthorizer authorizer,
			ModDbClient moddb,
			GameVersions versions,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadMods), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var found = await moddb.SearchAsync(q, ct).ConfigureAwait(false);
			if (!found.IsSuccess)
			{
				return Upstream(found.Failure!);
			}

			var game = await versions.OfAsync(Instance(http), ct).ConfigureAwait(false);
			return PanelResults.Json(new JsonObject { ["mods"] = found.Value, ["game_version"] = game.Text });
		});

		routes.MapGet("/api/moddb/mod/{identifier}", async (
			HttpContext http,
			string identifier,
			CommandAuthorizer authorizer,
			ModDbClient moddb,
			GameVersions versions,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadMods), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var game = await versions.OfAsync(Instance(http), ct).ConfigureAwait(false);
			var details = await moddb.DetailsAsync(identifier, game, string.Empty, ct).ConfigureAwait(false);
			return details.IsSuccess ? PanelResults.Json(details.Value.Json) : Upstream(details.Failure!);
		});

		// Только проверка: массового обновления без подтверждения нет (INV-28), каждый найденный
		// релиз ставится отдельным нажатием.
		routes.MapGet("/api/mods/updates", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IModStore mods,
			ModDbClient moddb,
			GameVersions versions,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadMods), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var listed = await mods.ListAsync(Instance(http), ct).ConfigureAwait(false);
			if (!listed.IsSuccess)
			{
				return PanelResults.Refuse(listed.Failure!);
			}

			var game = await versions.OfAsync(Instance(http), ct).ConfigureAwait(false);

			// Срез до отбора, как installed[:40] перед if item.get("modid").
			var candidates = listed.Value.Mods
				.Take(updateCheckLimit)
				.Where(mod => !string.IsNullOrEmpty(mod.ModId))
				.ToList();

			var updates = new JsonObject?[candidates.Count];
			await Parallel.ForEachAsync(
				Enumerable.Range(0, candidates.Count),
				new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
				async (index, token) =>
				{
					var mod = candidates[index];
					var release = await moddb
						.CompatibleReleaseAsync(mod.ModId!, game, includePrerelease: false, mod.DisplayName ?? string.Empty, token)
						.ConfigureAwait(false);

					updates[index] = release.IsSuccess
						? new JsonObject
						{
							["modid"] = mod.ModId,
							["installed"] = mod.Version,
							["available"] = release.Value.Release,
							["compatibility_unlisted"] = release.Value.Lookup.Reason == "no_compatible_release",
							["unavailable_reason"] = release.Value.Lookup.Reason,
						}
						: new JsonObject
						{
							["modid"] = mod.ModId,
							["installed"] = mod.Version,
							["error"] = release.Failure!.Message,
						};
				}).ConfigureAwait(false);

			var sorted = updates
				.Select(item => item!)
				.OrderBy(item => item["modid"]!.GetValue<string>().ToLowerInvariant(), StringComparer.Ordinal)
				.Select(item => (JsonNode) item);

			return PanelResults.Json(new JsonObject
			{
				["updates"] = new JsonArray([.. sorted]),
				["game_version"] = game.Text,
			});
		});

		routes.MapPost("/api/mods/install-release", async (
			HttpContext http,
			ModReleaseRequest body,
			CommandAuthorizer authorizer,
			ModDbClient moddb,
			GameVersions versions,
			IModStore mods,
			StagingWriter staging,
			PanelOptions options,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeMods));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			// Совместимость проверяется заново по свежему ответу ModDB, чтобы не скачать релиз, который
			// перестал быть совместимым после показа.
			var game = await versions.OfAsync(Instance(http), ct).ConfigureAwait(false);
			var download = await moddb.ResolveDownloadAsync(body.Identifier, body.ReleaseId, game, ct).ConfigureAwait(false);
			if (!download.IsSuccess)
			{
				return Upstream(download.Failure!);
			}

			var data = await moddb
				.DownloadAsync(download.Value.Url, (long) options.MaxUploadMegabytes * 1024 * 1024, ct)
				.ConfigureAwait(false);
			if (!data.IsSuccess)
			{
				return Upstream(data.Failure!);
			}

			var staged = StagingWriter.Reserve(StagedFile.Purpose.Mod, ".zip");
			try
			{
				using var source = new MemoryStream(data.Value, writable: false);
				var written = await staging.WriteAsync(source, staged, options.MaxUploadMegabytes, ct).ConfigureAwait(false);
				if (!written.IsSuccess)
				{
					return PanelResults.Refuse(written.Failure!);
				}

				var installed = await mods.InstallAsync(Instance(http), staged, ct).ConfigureAwait(false);
				if (installed.IsSuccess)
				{
					await audit
						.RecordAsync(http, "moddb_release_installed", $"{body.Identifier}:{body.ReleaseId}", ct)
						.ConfigureAwait(false);
				}

				return PanelResults.From(installed);
			}
			finally
			{
				staging.Discard(staged);
			}
		});
	}

	/// <summary>Отказ внешнего API: 502 с его текстом при любой причине.</summary>
	private static IResult Upstream(Failure failure) =>
		PanelResults.Refuse(StatusCodes.Status502BadGateway, failure.Message);

	/// <summary>Логический параметр запроса по правилам FastAPI: 1, true, on, yes в любом регистре.</summary>
	private static bool QueryFlag(string? value) =>
		value?.Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes" or "y" or "t";

	private static InstanceId Instance(HttpContext http) => PanelContext.Get(http)!.Instance;
}

/// <summary>Тело <c>POST /api/mods/install-release</c>.</summary>
public sealed record ModReleaseRequest
{
	[JsonPropertyName("identifier")] public required string Identifier { get; init; }

	[JsonPropertyName("release_id")] public required long ReleaseId { get; init; }
}
