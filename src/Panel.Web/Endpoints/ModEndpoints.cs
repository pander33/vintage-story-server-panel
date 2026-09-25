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

			var listed = await mods.ListAsync(Instance(http), ct).ConfigureAwait(false);
			if (!listed.IsSuccess)
			{
				return PanelResults.Refuse(listed.Failure!);
			}

			var game = await versions.OfAsync(Instance(http), ct).ConfigureAwait(false);
			return PanelResults.Json(ModDependencyReport.Annotate(listed.Value, game));
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
			// человек набирает по подсказке фронтенда (ADR-16).
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

	private static InstanceId Instance(HttpContext http) => PanelContext.Get(http)!.Instance;
}
