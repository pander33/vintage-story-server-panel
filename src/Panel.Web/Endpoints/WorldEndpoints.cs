using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Panel.Access.Authorization;
using Panel.Content.Domain;
using Panel.Content.Ports;
using Panel.Contracts.Content;
using Panel.Contracts.Http;
using Panel.SharedKernel;
using Panel.Web.Application;
using Panel.Web.Middleware;

namespace Panel.Web.Endpoints;

/// <summary>Миры: перечень, импорт, выгрузка, клонирование, архив и корзина.</summary>
public static class WorldEndpoints
{
	public static void MapWorlds(this IEndpointRouteBuilder routes)
	{
		routes.MapGet("/api/worlds", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IWorldStore worlds,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadWorlds), requireCsrf: false);

			return allowed.IsSuccess
				? PanelResults.From(await worlds.ListAsync(Instance(http), ct).ConfigureAwait(false))
				: PanelResults.Refuse(allowed.Failure!);
		});

		routes.MapPost("/api/worlds/upload", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IWorldStore worlds,
			StagingWriter staging,
			PanelOptions options,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeWorlds));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var form = await UploadLimits.ReadFormAsync(http, options.MaxWorldUploadMegabytes, ct).ConfigureAwait(false);
			if (form.TooLarge)
			{
				return PanelResults.Refuse(Failure.TooLarge($"Мир больше {options.MaxWorldUploadMegabytes} МБ"));
			}

			var file = form.Form?.Files.GetFile("file");

			if (file is null)
			{
				return PanelResults.Refuse(Failure.Invalid("Нужен файл мира .vcdbs"));
			}

			// Имя мира берётся из имени загруженного файла и проверяется здесь, потому что его прислал
			// клиент.
			var name = WorldFileName.Create(file.FileName);
			if (!name.IsSuccess)
			{
				return PanelResults.Refuse(name.Failure!);
			}

			var staged = StagingWriter.Reserve(StagedFile.Purpose.World, WorldFileName.Extension);
			try
			{
				await using var stream = file.OpenReadStream();
				var written = await staging
					.WriteAsync(stream, staged, options.MaxWorldUploadMegabytes, ct, $"Мир больше {options.MaxWorldUploadMegabytes} МБ")
					.ConfigureAwait(false);

				if (!written.IsSuccess)
				{
					return PanelResults.Refuse(written.Failure!);
				}

				var imported = await worlds
					.ImportAsync(Instance(http), staged, name.Value, ct)
					.ConfigureAwait(false);

				if (imported.IsSuccess)
				{
					await audit.RecordAsync(http, "world_uploaded", name.Value.Value, ct)
						.ConfigureAwait(false);
				}

				return PanelResults.From(imported);
			}
			finally
			{
				staging.Discard(staged);
			}
		});

		routes.MapGet("/api/worlds/download/{name}", async (
			HttpContext http,
			string name,
			CommandAuthorizer authorizer,
			IWorldStore worlds,
			StagingWriter staging,
			CancellationToken ct) =>
		{
			// Скачивание — это чтение, но всего мира. Право то же, что на просмотр перечня: в файле мира
			// нет ничего скрытого от наблюдателя, а ограничение выгрузки — вопрос нагрузки, а не доступа.
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadWorlds), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var world = WorldFileName.Create(name);
			if (!world.IsSuccess)
			{
				return PanelResults.Refuse(world.Failure!);
			}

			var staged = StagingWriter.Reserve(StagedFile.Purpose.World, WorldFileName.Extension);
			var exported = await worlds
				.ExportAsync(Instance(http), world.Value, staged, ct)
				.ConfigureAwait(false);

			if (!exported.IsSuccess)
			{
				staging.Discard(staged);
				return PanelResults.Refuse(exported.Failure!);
			}

			// Файл удаляется после отдачи: снимок нужен только на время скачивания, а весит столько же,
			// сколько мир.
			var path = staging.PathOf(staged);
			http.Response.OnCompleted(() =>
			{
				staging.Discard(staged);
				return Task.CompletedTask;
			});

			return Results.File(path, "application/octet-stream", world.Value.Value);
		});

		routes.MapPost("/api/worlds/clone", async (
			HttpContext http,
			WorldCloneRequest body,
			CommandAuthorizer authorizer,
			IWorldStore worlds,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeWorlds));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var source = WorldFileName.Create(body.Source);
			if (!source.IsSuccess)
			{
				return PanelResults.Refuse(source.Failure!);
			}

			var target = WorldFileName.Create(body.Name);
			if (!target.IsSuccess)
			{
				return PanelResults.Refuse(target.Failure!);
			}

			var cloned = await worlds
				.CloneAsync(Instance(http), source.Value, target.Value, ct)
				.ConfigureAwait(false);

			if (cloned.IsSuccess)
			{
				await audit
					.RecordAsync(http, "world_cloned", $"{body.Source}:{body.Name}", ct)
					.ConfigureAwait(false);
			}

			return PanelResults.From(cloned);
		});

		routes.MapPost("/api/worlds/create", async (
			HttpContext http,
			WorldCreateRequest body,
			CommandAuthorizer authorizer,
			IWorldStore worlds,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeWorlds));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var name = WorldFileName.CreateAddingExtension(body.FileName);
			if (!name.IsSuccess)
			{
				return PanelResults.Refuse(name.Failure!);
			}

			var style = PlayStyleWire.FromWire(body.PlayStyle);
			if (!style.IsSuccess)
			{
				return PanelResults.Refuse(style.Failure!);
			}

			var plan = new WorldPlan(name.Value, body.WorldName.Trim(), body.Seed.Trim(), style.Value);
			var created = await worlds.CreateAsync(Instance(http), plan, ct).ConfigureAwait(false);

			if (created.IsSuccess)
			{
				await audit
					.RecordAsync(http, "world_creation_configured", name.Value.Value, ct)
					.ConfigureAwait(false);
			}

			return PanelResults.From(created);
		});

		routes.MapPost("/api/worlds/action", async (
			HttpContext http,
			WorldActionRequest body,
			CommandAuthorizer authorizer,
			IWorldStore worlds,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeWorlds));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var name = WorldFileName.Create(body.Name);
			if (!name.IsSuccess)
			{
				return PanelResults.Refuse(name.Failure!);
			}

			var instance = Instance(http);

			if (body.Action == "activate")
			{
				var activated = await worlds.ActivateAsync(instance, name.Value, ct).ConfigureAwait(false);
				if (activated.IsSuccess)
				{
					await audit.RecordAsync(http, "world_activate", body.Name, ct)
						.ConfigureAwait(false);
				}

				return PanelResults.From(activated);
			}

			var disposition = WorldDispositionWire.FromWire(body.Action);
			if (!disposition.IsSuccess)
			{
				return PanelResults.Refuse(Failure.Invalid("Недопустимое действие с миром"));
			}

			var confirmed = WorldDispositionWire
				.ConfirmationFor(disposition.Value, name.Value)
				.Check(body.Confirmation);

			if (!confirmed.IsSuccess)
			{
				return PanelResults.Refuse(confirmed.Failure!);
			}

			var stored = await worlds
				.PutAwayAsync(instance, name.Value, disposition.Value, ct)
				.ConfigureAwait(false);

			if (stored.IsSuccess)
			{
				await audit.RecordAsync(http, "world_" + body.Action, body.Name, ct)
					.ConfigureAwait(false);
			}

			return PanelResults.From(stored);
		});

		MapStored(routes);
	}

	/// <summary>Архив и корзина: перечень, возврат и окончательное удаление.</summary>
	private static void MapStored(IEndpointRouteBuilder routes)
	{
		routes.MapGet("/api/worlds/archives", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IWorldStore worlds,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadWorlds), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var stored = await worlds.ListStoredAsync(Instance(http), ct).ConfigureAwait(false);

			return stored.Match(
				value => PanelResults.Json(new { worlds = value }),
				PanelResults.Refuse);
		});

		routes.MapPost("/api/worlds/restore", async (
			HttpContext http,
			WorldRestoreRequest body,
			CommandAuthorizer authorizer,
			IWorldStore worlds,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeWorlds));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var disposition = WorldDispositionWire.FromWire(body.Mode);
			if (!disposition.IsSuccess)
			{
				return PanelResults.Refuse(disposition.Failure!);
			}

			var stored = StoredWorldName.Create(body.StoredName);
			if (!stored.IsSuccess)
			{
				return PanelResults.Refuse(stored.Failure!);
			}

			var target = WorldFileName.Create(body.TargetName);
			if (!target.IsSuccess)
			{
				return PanelResults.Refuse(target.Failure!);
			}

			// Подтверждается имя, под которым мир вернётся, а не имя в архиве: набирающий должен видеть
			// результат действия.
			var confirmed = Confirmation.ToRestore(target.Value.Value).Check(body.Confirmation);
			if (!confirmed.IsSuccess)
			{
				return PanelResults.Refuse(confirmed.Failure!);
			}

			var restored = await worlds
				.RestoreStoredAsync(Instance(http), disposition.Value, stored.Value, target.Value, ct)
				.ConfigureAwait(false);

			if (restored.IsSuccess)
			{
				await audit
					.RecordAsync(http, "world_restored",
						$"{body.Mode}:{body.StoredName}:{body.TargetName}",
						ct)
					.ConfigureAwait(false);
			}

			return PanelResults.From(restored);
		});

		routes.MapPost("/api/worlds/archives/delete", async (
			HttpContext http,
			ArchivedWorldRequest body,
			CommandAuthorizer authorizer,
			IWorldStore worlds,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeWorlds));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var disposition = WorldDispositionWire.FromWire(body.Mode);
			if (!disposition.IsSuccess)
			{
				return PanelResults.Refuse(disposition.Failure!);
			}

			var stored = StoredWorldName.Create(body.StoredName);
			if (!stored.IsSuccess)
			{
				return PanelResults.Refuse(stored.Failure!);
			}

			// Единственное безвозвратное удаление содержимого: у архива и корзины нет уровня ниже.
			// Набирается точное хранимое имя.
			var confirmed = Confirmation.ToDelete(stored.Value.Value).Check(body.Confirmation);
			if (!confirmed.IsSuccess)
			{
				return PanelResults.Refuse(confirmed.Failure!);
			}

			var deleted = await worlds
				.DeleteStoredAsync(Instance(http), disposition.Value, stored.Value, ct)
				.ConfigureAwait(false);

			if (deleted.IsSuccess)
			{
				await audit
					.RecordAsync(http, "world_archive_deleted",
						$"{body.Mode}:{body.StoredName}",
						ct)
					.ConfigureAwait(false);
			}

			return PanelResults.From(deleted);
		});
	}

	private static InstanceId Instance(HttpContext http) => PanelContext.Get(http)!.Instance;
}
