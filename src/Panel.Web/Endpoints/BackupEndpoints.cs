using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Panel.Access.Authorization;
using Panel.Content.Domain;
using Panel.Content.Ports;
using Panel.Contracts.Http;
using Panel.SharedKernel;
using Panel.Web.Application;
using Panel.Web.Middleware;

namespace Panel.Web.Endpoints;

/// <summary>Резервные копии: снятие, разбор, восстановление, удержание.</summary>
public static class BackupEndpoints
{
	public static void MapBackups(this IEndpointRouteBuilder routes)
	{
		routes.MapGet("/api/backups", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IBackupStore backups,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadBackups), requireCsrf: false);

			return allowed.IsSuccess
				? PanelResults.From(await backups.ListAsync(Instance(http), ct).ConfigureAwait(false))
				: PanelResults.Refuse(allowed.Failure!);
		});

		routes.MapPost("/api/backups", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IBackupStore backups,
			AuditLog audit,
			CancellationToken ct) =>
		{
			// Доступно оператору: копию снимают перед любым рискованным действием, и требование прав
			// администратора подталкивало бы действовать без неё.
			var allowed = PanelResults.Authorize(http, authorizer, typeof(CreateBackup));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var created = await backups.CreateAsync(Instance(http), offline: false, ct).ConfigureAwait(false);
			if (created.IsSuccess)
			{
				await audit
					.RecordAsync(http, "backup_created", created.Value.Name ?? "", ct)
					.ConfigureAwait(false);
			}

			return PanelResults.From(created);
		});

		routes.MapGet("/api/backups/{name}/inspect", async (
			HttpContext http,
			string name,
			CommandAuthorizer authorizer,
			IBackupStore backups,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadBackups), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var backup = BackupName.Create(name);

			return backup.IsSuccess
				? PanelResults.From(await backups.InspectAsync(Instance(http), backup.Value, ct).ConfigureAwait(false))
				: PanelResults.Refuse(backup.Failure!);
		});

		routes.MapPost("/api/backups/restore", async (
			HttpContext http,
			ConfirmedNameRequest body,
			CommandAuthorizer authorizer,
			IBackupStore backups,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeBackups));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var name = BackupName.Create(body.Name);
			if (!name.IsSuccess)
			{
				return PanelResults.Refuse(name.Failure!);
			}

			var confirmed = Confirmation.ToRestore(name.Value.Value).Check(body.Confirmation);
			if (!confirmed.IsSuccess)
			{
				return PanelResults.Refuse(confirmed.Failure!);
			}

			var restored = await backups.RestoreAsync(Instance(http), name.Value, ct).ConfigureAwait(false);
			if (restored.IsSuccess)
			{
				await audit.RecordAsync(http, "backup_restored", body.Name, ct)
					.ConfigureAwait(false);
			}

			return PanelResults.From(restored);
		});

		routes.MapPost("/api/backups/delete", async (
			HttpContext http,
			ConfirmedNameRequest body,
			CommandAuthorizer authorizer,
			IBackupStore backups,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeBackups));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var name = BackupName.Create(body.Name);
			if (!name.IsSuccess)
			{
				return PanelResults.Refuse(name.Failure!);
			}

			var confirmed = Confirmation.ToDelete(name.Value.Value).Check(body.Confirmation);
			if (!confirmed.IsSuccess)
			{
				return PanelResults.Refuse(confirmed.Failure!);
			}

			var deleted = await backups.DeleteAsync(Instance(http), name.Value, ct).ConfigureAwait(false);
			if (deleted.IsSuccess)
			{
				await audit.RecordAsync(http, "backup_deleted", body.Name, ct)
					.ConfigureAwait(false);
			}

			return PanelResults.From(deleted);
		});

		routes.MapPost("/api/backups/restore-world", async (
			HttpContext http,
			BackupWorldRestoreRequest body,
			CommandAuthorizer authorizer,
			IBackupStore backups,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeBackups));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var name = BackupName.Create(body.Backup);
			if (!name.IsSuccess)
			{
				return PanelResults.Refuse(name.Failure!);
			}

			var target = WorldFileName.Create(body.TargetName);
			if (!target.IsSuccess)
			{
				return PanelResults.Refuse(target.Failure!);
			}

			// Подтверждается глаголом ИЗВЛЕЧЬ и именем, под которым появится мир.
			var confirmed = Confirmation.ToExtract(target.Value.Value).Check(body.Confirmation);
			if (!confirmed.IsSuccess)
			{
				return PanelResults.Refuse(confirmed.Failure!);
			}

			var restored = await backups
				.RestoreWorldAsync(Instance(http), name.Value, body.World, target.Value, ct)
				.ConfigureAwait(false);

			if (restored.IsSuccess)
			{
				await audit
					.RecordAsync(http, "backup_world_restored",
						$"{body.Backup}:{body.World}:{body.TargetName}",
						ct)
					.ConfigureAwait(false);
			}

			return PanelResults.From(restored);
		});

		routes.MapPost("/api/backups/retention", async (
			HttpContext http,
			RetentionRequest body,
			CommandAuthorizer authorizer,
			IBackupStore backups,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeBackups));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var policy = RetentionPolicy.Create(body.KeepCount, body.KeepDays, body.MinimumFreeGigabytes);
			if (!policy.IsSuccess)
			{
				return PanelResults.Refuse(policy.Failure!);
			}

			// Предпросмотр подтверждения не требует: он ничего не удаляет.
			if (body.Apply)
			{
				var confirmed = Confirmation.Exactly("ОЧИСТИТЬ СТАРЫЕ КОПИИ").Check(body.Confirmation);
				if (!confirmed.IsSuccess)
				{
					return PanelResults.Refuse(confirmed.Failure!);
				}
			}

			var outcome = await backups
				.ApplyRetentionAsync(Instance(http), policy.Value, body.Apply, ct)
				.ConfigureAwait(false);

			if (outcome.IsSuccess && body.Apply)
			{
				await audit
					.RecordAsync(http, "backup_retention_applied",
						string.Join(", ", outcome.Value.Removed),
						ct)
					.ConfigureAwait(false);
			}

			return PanelResults.From(outcome);
		});
	}

	private static InstanceId Instance(HttpContext http) => PanelContext.Get(http)!.Instance;
}
