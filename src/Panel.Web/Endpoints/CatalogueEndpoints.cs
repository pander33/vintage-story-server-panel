using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Panel.Access.Authorization;
using Panel.Catalogues.Domain;
using Panel.Catalogues.Infrastructure;
using Panel.Content.Ports;
using Panel.Contracts.Content;
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

		// Каталог: список целиком держит панель, отбирает и сортирует тоже она, браузер получает страницу.
		routes.MapGet("/api/moddb/mods", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			ModCatalogueCache catalogue,
			IModStore mods,
			GameVersions versions,
			CancellationToken ct,
			string? sort = null,
			[FromQuery(Name = "tag")] string[]? tag = null,
			string? side = null,
			string? compatible = null,
			[FromQuery(Name = "hide_installed")] string? hideInstalled = null,
			string? q = null,
			int? offset = null,
			int? limit = null) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadMods), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var game = await versions.OfAsync(Instance(http), ct).ConfigureAwait(false);
			var snapshot = await catalogue.SnapshotAsync(game, ct).ConfigureAwait(false);
			if (!snapshot.IsSuccess)
			{
				return Upstream(snapshot.Failure!);
			}

			// Установленные моды — пометка на карточке и отбор. Демон недоступен — каталог всё равно
			// показывается, только без пометок.
			var listed = await mods.ListAsync(Instance(http), ct).ConfigureAwait(false);
			var installed = listed.IsSuccess ? Installed(listed.Value) : null;
			var installedIds = installed?.Keys.ToHashSet(StringComparer.Ordinal);

			var query = CatalogueQuery.ParseParameters(sort, tag ?? [], side, QueryFlag(compatible), q, offset, limit)
				with
			{ HideInstalled = QueryFlag(hideInstalled) };
			var page = ModCatalogue.Page(snapshot.Value.Entries, query, snapshot.Value.Compatible, installedIds);
			return PanelResults.Json(new JsonObject
			{
				["mods"] = new JsonArray([.. page.Items.Select(entry => (JsonNode) Card(entry, snapshot.Value.Compatible, installed))]),
				["total"] = page.Total,
				["offset"] = query.Offset,
				["limit"] = query.Limit,
				["game_version"] = game.Text,
				["compatible_known"] = snapshot.Value.Compatible is not null,
				["installed_known"] = installed is not null,
				["stale"] = snapshot.Value.Stale,
				["available_tags"] = new JsonArray([.. ModCatalogue.PopularTags(snapshot.Value.Entries, snapshot.Value.Tags).Select(item => (JsonNode) new JsonObject
				{
					["name"] = item.Tag.Name,
					["color"] = item.Tag.Color,
					["count"] = item.Count,
				})]),
			});
		});

		// Картинки ModDB: CSP разрешает только свой хост, а no-store не даёт браузеру их сохранить.
		routes.MapGet("/api/moddb/image", async (
			HttpContext http,
			string url,
			CommandAuthorizer authorizer,
			ModImageProxy images,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadMods), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var image = await images.GetAsync(url, ct).ConfigureAwait(false);
			if (image.IsSuccess)
			{
				return Results.Bytes(image.Value.Data, image.Value.ContentType);
			}

			return image.Failure!.Code == FailureCode.Invalid ? PanelResults.Refuse(image.Failure!) : Upstream(image.Failure!);
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

		// Только проверка: массового обновления без подтверждения нет, каждый найденный
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
							["available_prerelease"] = release.Value.Prerelease,
							["compatibility_unlisted"] = release.Value.Lookup.Reason == "no_compatible_release",
							["unavailable_reason"] = release.Value.Lookup.Reason,
						}
						: new JsonObject
						{
							["modid"] = mod.ModId,
							["installed"] = mod.Version,
							["error"] = PhraseJsonConverter.ToNode(release.Failure!.Text),
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

	/// <summary>Установленные моды по <c>modid</c> в нижнем регистре; у повторов — первый по списку.</summary>
	private static Dictionary<string, ModEntryPayload> Installed(ModsPayload listed)
	{
		var installed = new Dictionary<string, ModEntryPayload>(StringComparer.Ordinal);
		foreach (var mod in listed.Mods.Where(mod => !string.IsNullOrEmpty(mod.ModId)))
		{
			installed.TryAdd(mod.ModId!.ToLowerInvariant(), mod);
		}

		return installed;
	}

	private static JsonObject Card(CatalogueEntry entry, IReadOnlySet<long>? compatible, Dictionary<string, ModEntryPayload>? installed) => new()
	{
		["modid"] = entry.ModId,
		["identifier"] = entry.LookupKey,
		["modidstrs"] = new JsonArray([.. entry.Identifiers.Select(id => (JsonNode) JsonValue.Create(id)!)]),
		["name"] = entry.Name,
		["summary"] = entry.Summary,
		["author"] = entry.Author,
		["side"] = entry.Side,
		["tags"] = new JsonArray([.. entry.Tags.Select(name => (JsonNode) JsonValue.Create(name)!)]),
		["downloads"] = entry.Downloads,
		["follows"] = entry.Follows,
		["lastreleased"] = entry.LastReleased,
		["logo"] = ModDbClient.ProxiedImage(entry.Logo),
		["compatible"] = compatible is null ? null : compatible.Contains(entry.ModId),
		["installed"] = installed is null
			|| entry.Identifiers.Select(id => installed.GetValueOrDefault(id.ToLowerInvariant())).FirstOrDefault(mod => mod is not null) is not { } mod
			? null
			: new JsonObject { ["version"] = mod.Version, ["enabled"] = mod.Enabled },
	};

	/// <summary>Отказ внешнего API: 502 с его текстом при любой причине.</summary>
	private static IResult Upstream(Failure failure) =>
		PanelResults.Refuse(StatusCodes.Status502BadGateway, failure.Text);

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
