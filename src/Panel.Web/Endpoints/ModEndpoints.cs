using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Panel.Access.Authorization;
using Panel.Content.Domain;
using Panel.Content.Ports;
using Panel.Contracts;
using Panel.Contracts.Http;
using Panel.SharedKernel;
using Panel.Web.Application;
using Panel.Web.Middleware;

namespace Panel.Web.Endpoints;

/// <summary>Моды и их настройки.</summary>
public static class ModEndpoints
{
	public static void MapMods(this IEndpointRouteBuilder routes)
	{
		routes.MapGet("/api/mods", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IModStore mods,
			GameVersions versions,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadMods), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var listed = await ListAsync(http, mods, versions, ct).ConfigureAwait(false);

			return listed.IsSuccess ? PanelResults.Json(listed.Value) : PanelResults.Refuse(listed.Failure!);
		});

		routes.MapGet("/api/mods/export.csv", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IModStore mods,
			GameVersions versions,
			TimeProvider clock,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadMods), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var listed = await ListAsync(http, mods, versions, ct).ConfigureAwait(false);
			if (!listed.IsSuccess)
			{
				return PanelResults.Refuse(listed.Failure!);
			}

			// Имя по местному времени панели, как у выгрузки журнала.
			var stamp = clock.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
			http.Response.Headers.ContentDisposition = $"attachment; filename=\"mods-{Instance(http).Value}-{stamp}.csv\"";

			return Results.Bytes(ModListCsv.Build(listed.Value, LocalizedJson.LanguageOf(http)), "text/csv; charset=utf-8");
		});

		routes.MapPost("/api/mods/upload", async (
			HttpContext http,
			CommandAuthorizer authorizer,
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

			var form = await UploadLimits.ReadFormAsync(http, options.MaxUploadMegabytes, ct).ConfigureAwait(false);
			if (form.TooLarge)
			{
				return PanelResults.Refuse(Failure.TooLarge($"Файл больше {options.MaxUploadMegabytes} МБ"));
			}

			var file = form.Form?.Files.GetFile("file");

			if (file is null || !file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
			{
				return PanelResults.Refuse(Failure.Invalid("Нужен ZIP-архив мода"));
			}

			var staged = StagingWriter.Reserve(StagedFile.Purpose.Mod, ".zip");
			try
			{
				await using var stream = file.OpenReadStream();
				var written = await staging
					.WriteAsync(stream, staged, options.MaxUploadMegabytes, ct)
					.ConfigureAwait(false);

				if (!written.IsSuccess)
				{
					return PanelResults.Refuse(written.Failure!);
				}

				var installed = await mods.InstallAsync(Instance(http), staged, ct).ConfigureAwait(false);
				if (installed.IsSuccess)
				{
					await audit.RecordAsync(http, "mod_uploaded", installed.Value.Name, ct)
						.ConfigureAwait(false);
				}

				return PanelResults.From(installed);
			}
			finally
			{
				// Временный файл удаляется в любом случае: демон его уже скопировал или он не понадобился.
				staging.Discard(staged);
			}
		});

		routes.MapPost("/api/mods/toggle", async (
			HttpContext http,
			NameRequest body,
			CommandAuthorizer authorizer,
			IModStore mods,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeMods));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var name = ModArchiveName.Create(body.Name);
			if (!name.IsSuccess)
			{
				return PanelResults.Refuse(name.Failure!);
			}

			var toggled = await mods.ToggleAsync(Instance(http), name.Value, ct).ConfigureAwait(false);
			if (toggled.IsSuccess)
			{
				await audit.RecordAsync(http, "mod_toggled", body.Name, ct)
					.ConfigureAwait(false);
			}

			return PanelResults.From(toggled);
		});

		routes.MapPost("/api/mods/delete", async (
			HttpContext http,
			ConfirmedNameRequest body,
			CommandAuthorizer authorizer,
			IModStore mods,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeMods));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var name = ModArchiveName.Create(body.Name);
			if (!name.IsSuccess)
			{
				return PanelResults.Refuse(name.Failure!);
			}

			// В отличие от остальных подтверждений, здесь ожидается только имя файла, без глагола: фразу
			// человек набирает по подсказке фронтенда.
			var confirmed = Confirmation.ByNameAlone(name.Value.Value).Check(body.Confirmation);
			if (!confirmed.IsSuccess)
			{
				return PanelResults.Refuse(Failure.Invalid("Для удаления введите имя файла мода полностью"));
			}

			var deleted = await mods.DeleteAsync(Instance(http), name.Value, ct).ConfigureAwait(false);
			if (deleted.IsSuccess)
			{
				await audit.RecordAsync(http, "mod_deleted", body.Name, ct)
					.ConfigureAwait(false);
			}

			return PanelResults.From(deleted);
		});

		routes.MapPost("/api/mods/rollback", async (
			HttpContext http,
			ModRollbackRequest body,
			CommandAuthorizer authorizer,
			IModStore mods,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeMods));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var mod = ModId.Create(body.ModId);
			if (!mod.IsSuccess)
			{
				return PanelResults.Refuse(mod.Failure!);
			}

			var archive = ModArchiveName.Create(body.Archive);
			if (!archive.IsSuccess)
			{
				return PanelResults.Refuse(archive.Failure!);
			}

			var rolled = await mods
				.RollbackAsync(Instance(http), mod.Value, archive.Value, ct)
				.ConfigureAwait(false);

			if (rolled.IsSuccess)
			{
				await audit
					.RecordAsync(http, "mod_rollback", $"{body.ModId}:{body.Archive}", ct)
					.ConfigureAwait(false);
			}

			return PanelResults.From(rolled);
		});

		MapProfiles(routes);
		MapConfigs(routes);
		MapPacks(routes);
	}

	/// <summary>Сборка модов: опись модов и файлы настроек одним ZIP.</summary>
	private static void MapPacks(IEndpointRouteBuilder routes)
	{
		// Настройки модов бывают с ключами и паролями, поэтому выгрузка — право администратора, как и их чтение.
		routes.MapGet("/api/mods/pack", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IModStore mods,
			GameVersions versions,
			TimeProvider clock,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadModConfigs), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var game = await versions.OfAsync(Instance(http), ct).ConfigureAwait(false);
			var pack = await ModPackExchange
				.ExportAsync(mods, Instance(http), GameVersionText.From(game.Text), clock.GetUtcNow(), ct)
				.ConfigureAwait(false);
			if (!pack.IsSuccess)
			{
				return PanelResults.Refuse(pack.Failure!);
			}

			var stamp = clock.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
			http.Response.Headers.ContentDisposition = $"attachment; filename=\"modpack-{Instance(http).Value}-{stamp}.zip\"";

			return Results.Bytes(pack.Value, "application/zip");
		});

		// Разбор ничего не меняет, но нужен только перед применением: право то же, и во время
		// обслуживания он так же отвечает 409.
		routes.MapPost("/api/mods/pack/inspect", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IModStore mods,
			GameVersions versions,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeMods));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var contents = await ReadPackAsync(http, ct).ConfigureAwait(false);
			if (!contents.IsSuccess)
			{
				return PanelResults.Refuse(contents.Failure!);
			}

			var game = await versions.OfAsync(Instance(http), ct).ConfigureAwait(false);
			return PanelResults.From(await ModPackExchange
				.PlanAsync(mods, Instance(http), contents.Value, GameVersionText.From(game.Text), ct)
				.ConfigureAwait(false));
		});

		// Сборка присылается заново, а план пересчитывается: клиент выбирает только, что применить,
		// а какие архивы переключать, решает сервер по своему состоянию.
		routes.MapPost("/api/mods/pack/apply", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IModStore mods,
			GameVersions versions,
			StagingWriter staging,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeMods));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var configsAllowed = PanelResults.Authorize(http, authorizer, typeof(ChangeModConfigs));
			if (!configsAllowed.IsSuccess)
			{
				return PanelResults.Refuse(configsAllowed.Failure!);
			}

			var contents = await ReadPackAsync(http, ct).ConfigureAwait(false);
			if (!contents.IsSuccess)
			{
				return PanelResults.Refuse(contents.Failure!);
			}

			var game = await versions.OfAsync(Instance(http), ct).ConfigureAwait(false);
			var plan = await ModPackExchange
				.PlanAsync(mods, Instance(http), contents.Value, GameVersionText.From(game.Text), ct)
				.ConfigureAwait(false);
			if (!plan.IsSuccess)
			{
				return PanelResults.Refuse(plan.Failure!);
			}

			var form = http.Request.Form;
			var chosen = ChosenConfigs(form["configs"].ToString());
			if (!chosen.IsSuccess)
			{
				return PanelResults.Refuse(chosen.Failure!);
			}

			var configs = contents.Value.Files.Keys.Where(name => chosen.Value.Contains(name.Value)).OrderBy(name => name.Value, StringComparer.Ordinal).ToList();
			var toggles = ModPack.Toggles(plan.Value, form["toggle"] == "true", form["disable_extra"] == "true");

			var applied = await ModPackExchange
				.ApplyAsync(mods, staging, Instance(http), contents.Value, configs, toggles, ct)
				.ConfigureAwait(false);

			if (applied.Configs.Count > 0 || applied.Toggled.Count > 0)
			{
				await audit.RecordAsync(http, "modpack_applied",
					$"{contents.Value.Manifest.Server}: configs {string.Join(", ", applied.Configs)}; toggled {string.Join(", ", applied.Toggled)}", ct)
					.ConfigureAwait(false);
			}

			return PanelResults.Json(applied);
		});
	}

	/// <summary>Файл сборки из формы, прочитанный в память целиком: он не больше 32 МБ.</summary>
	private static async Task<Result<ModPackContents>> ReadPackAsync(HttpContext http, CancellationToken ct)
	{
		var form = await UploadLimits.ReadFormAsync(http, ModPack.MaximumPackMegabytes, ct).ConfigureAwait(false);
		if (form.TooLarge)
		{
			return Failure.TooLarge($"Сборка больше {ModPack.MaximumPackMegabytes} МБ");
		}

		var file = form.Form?.Files.GetFile("file");
		if (file is null)
		{
			return Failure.Invalid("Нужен файл сборки модов (.zip)");
		}

		if (file.Length > ModPack.MaximumPackMegabytes * 1024L * 1024)
		{
			return Failure.TooLarge($"Сборка больше {ModPack.MaximumPackMegabytes} МБ");
		}

		using var buffer = new MemoryStream();
		await using (var stream = file.OpenReadStream())
		{
			await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
		}

		buffer.Position = 0;
		return ModPackExchange.Read(buffer);
	}

	/// <summary>Имена выбранных настроек: JSON-массив строк из поля формы <c>configs</c>.</summary>
	private static Result<HashSet<string>> ChosenConfigs(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return new HashSet<string>(StringComparer.Ordinal);
		}

		try
		{
			return (JsonSerializer.Deserialize<List<string>>(raw) ?? []).ToHashSet(StringComparer.Ordinal);
		}
		catch (JsonException)
		{
			return Failure.Invalid("Список настроек сборки должен быть JSON-массивом имён");
		}
	}

	private static void MapProfiles(IEndpointRouteBuilder routes)
	{
		routes.MapPost("/api/mods/profile/save", async (
			HttpContext http,
			NameRequest body,
			CommandAuthorizer authorizer,
			IModStore mods,
			AuditLog audit,
			CancellationToken ct) =>
			await ProfileAsync(http, body, authorizer, audit, "mod_profile_saved", ct,
				(instance, profile) => mods.SaveProfileAsync(instance, profile, ct)).ConfigureAwait(false));

		routes.MapPost("/api/mods/profile/apply", async (
			HttpContext http,
			NameRequest body,
			CommandAuthorizer authorizer,
			IModStore mods,
			AuditLog audit,
			CancellationToken ct) =>
			await ProfileAsync(http, body, authorizer, audit, "mod_profile_applied", ct,
				(instance, profile) => mods.ApplyProfileAsync(instance, profile, ct)).ConfigureAwait(false));

		routes.MapPost("/api/mods/profile/delete", async (
			HttpContext http,
			NameRequest body,
			CommandAuthorizer authorizer,
			IModStore mods,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeMods));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var profile = ModProfileName.Create(body.Name);
			if (!profile.IsSuccess)
			{
				return PanelResults.Refuse(profile.Failure!);
			}

			// Удаляется только профиль, архивы модов не затрагиваются.
			var deleted = await mods.DeleteProfileAsync(Instance(http), profile.Value, ct).ConfigureAwait(false);
			if (!deleted.IsSuccess)
			{
				return PanelResults.Refuse(deleted.Failure!);
			}

			await audit.RecordAsync(http, "mod_profile_deleted", body.Name, ct)
				.ConfigureAwait(false);

			var remaining = await mods.ListAsync(Instance(http), ct).ConfigureAwait(false);

			return PanelResults.Json(new Contracts.Content.ModProfilePayload
			{
				Profile = profile.Value.Value,
				Remaining = remaining.IsSuccess ? remaining.Value.Profiles : [],
				RestartRequired = false,
			});
		});
	}

	private static void MapConfigs(IEndpointRouteBuilder routes)
	{
		routes.MapGet("/api/mod-configs", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IModStore mods,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadModConfigs), requireCsrf: false);

			return allowed.IsSuccess
				? PanelResults.From(await mods.ListConfigsAsync(Instance(http), ct).ConfigureAwait(false))
				: PanelResults.Refuse(allowed.Failure!);
		});

		routes.MapGet("/api/mod-configs/{name}", async (
			HttpContext http,
			string name,
			CommandAuthorizer authorizer,
			IModStore mods,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadModConfigs), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var file = ModConfigName.Create(name);

			return file.IsSuccess
				? PanelResults.From(await mods.ReadConfigAsync(Instance(http), file.Value, ct).ConfigureAwait(false))
				: PanelResults.Refuse(file.Failure!);
		});

		routes.MapPut("/api/mod-configs/{name}", async (
			HttpContext http,
			string name,
			JsonConfigRequest body,
			CommandAuthorizer authorizer,
			IModStore mods,
			StagingWriter staging,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeModConfigs));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var file = ModConfigName.Create(name);
			if (!file.IsSuccess)
			{
				return PanelResults.Refuse(file.Failure!);
			}

			if (body.Config is null)
			{
				return PanelResults.Refuse(Failure.Invalid("Настройки мода пусты"));
			}

			// Метка изменения проверяется перед записью: ноль означает «не проверять», а несовпадение —
			// что файл правили в обход панели и сохранение перезаписало бы эту правку.
			if (body.Revision != 0)
			{
				var current = await mods.ReadConfigAsync(Instance(http), file.Value, ct).ConfigureAwait(false);
				if (!current.IsSuccess)
				{
					return PanelResults.Refuse(current.Failure!);
				}

				if (current.Value.ModifiedNanoseconds != body.Revision)
				{
					return PanelResults.Refuse(
						Failure.Conflict("Файл настроек мода изменился; перечитайте его"));
				}
			}

			var staged = StagingWriter.Reserve(StagedFile.Purpose.Config, ".json");
			try
			{
				var json = body.Config.ToJsonString(PanelJson.Indented);
				await using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
				{
					var written = await staging.WriteAsync(stream, staged, 8, ct).ConfigureAwait(false);
					if (!written.IsSuccess)
					{
						return PanelResults.Refuse(written.Failure!);
					}
				}

				var saved = await mods
					.WriteConfigAsync(Instance(http), file.Value, staged, ct)
					.ConfigureAwait(false);

				if (!saved.IsSuccess)
				{
					return PanelResults.Refuse(saved.Failure!);
				}

				await audit.RecordAsync(http, "mod_config_saved", name, ct)
					.ConfigureAwait(false);

				return PanelResults.Json(new Contracts.Content.ModChangePayload { Name = file.Value.Value });
			}
			finally
			{
				staging.Discard(staged);
			}
		});
	}

	private static async Task<IResult> ProfileAsync(
		HttpContext http,
		NameRequest body,
		CommandAuthorizer authorizer,
		AuditLog audit,
		string auditEvent,
		CancellationToken ct,
		Func<InstanceId, ModProfileName, Task<Result<Contracts.Content.ModProfilePayload>>> action)
	{
		var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeMods));
		if (!allowed.IsSuccess)
		{
			return PanelResults.Refuse(allowed.Failure!);
		}

		var profile = ModProfileName.Create(body.Name);
		if (!profile.IsSuccess)
		{
			return PanelResults.Refuse(profile.Failure!);
		}

		var result = await action(Instance(http), profile.Value).ConfigureAwait(false);
		if (result.IsSuccess)
		{
			await audit.RecordAsync(http, auditEvent, body.Name, ct)
				.ConfigureAwait(false);
		}

		return PanelResults.From(result);
	}

	/// <summary>Моды от демона с оценкой зависимостей по версии игры сервера.</summary>
	private static async Task<Result<Contracts.Content.ModsPayload>> ListAsync(
		HttpContext http,
		IModStore mods,
		GameVersions versions,
		CancellationToken ct)
	{
		var listed = await mods.ListAsync(Instance(http), ct).ConfigureAwait(false);
		if (!listed.IsSuccess)
		{
			return listed;
		}

		var game = await versions.OfAsync(Instance(http), ct).ConfigureAwait(false);
		return ModDependencyReport.Annotate(listed.Value, game);
	}

	private static InstanceId Instance(HttpContext http) => PanelContext.Get(http)!.Instance;
}
