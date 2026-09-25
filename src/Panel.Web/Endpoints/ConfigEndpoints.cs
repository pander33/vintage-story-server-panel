using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Panel.Access.Authorization;
using Panel.Access.Domain;
using Panel.Configuration.Domain;
using Panel.Contracts.Http;
using Panel.Contracts.ServerCommands;
using Panel.Runtime.Ports;
using Panel.SharedKernel;
using Panel.Web.Application;
using Panel.Web.Middleware;

namespace Panel.Web.Endpoints;

/// <summary>
/// Настройки сервера: <c>serverconfig.json</c>, шаблон нового мира и живая конфигурация
/// работающего мира.
/// </summary>
/// <remarks>
/// Шаблон и живая конфигурация рядом по адресам, но это разные типы: шаблон пишется в файл, а
/// живая конфигурация меняется только командой игры (INV-20).
/// </remarks>
public static class ConfigEndpoints
{
	public static void MapConfiguration(this IEndpointRouteBuilder routes)
	{
		routes.MapGet("/api/config", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			ServerConfigurationHandler handler,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadServerConfig), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			// Пароль входа на сервер видит только тот, кто может его менять.
			var redact = !allowed.Value.Can(Permission.Administer);

			return PanelResults.From(await handler.DescribeAsync(Instance(http), redact, ct).ConfigureAwait(false));
		});

		routes.MapPost("/api/config/preview", async (
			HttpContext http,
			ConfigRequest body,
			CommandAuthorizer authorizer,
			ServerConfigurationHandler handler,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeServerConfig));

			return allowed.IsSuccess
				? PanelResults.From(await handler.PreviewAsync(Instance(http), body, ct).ConfigureAwait(false))
				: PanelResults.Refuse(allowed.Failure!);
		});

		routes.MapPut("/api/config", async (
			HttpContext http,
			ConfigRequest body,
			CommandAuthorizer authorizer,
			ServerConfigurationHandler handler,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeServerConfig));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var saved = await handler.SaveAsync(Instance(http), body, ct).ConfigureAwait(false);

			// Сохранение без изменений завершается до аудита. Признак записи — restart_required: демон
			// ставит его при каждой записи, а ответ «изменений нет» содержит false. Наличие копии для
			// этого не подходит: её нет, если файла до сохранения не было.
			if (saved.IsSuccess && saved.Value.RestartRequired)
			{
				await audit.RecordAsync(http, "config_saved", string.Empty, ct)
					.ConfigureAwait(false);
			}

			return PanelResults.From(saved);
		});

		routes.MapPost("/api/config/rollback", async (
			HttpContext http,
			NameRequest body,
			CommandAuthorizer authorizer,
			ServerConfigurationHandler handler,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeServerConfig));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var name = ConfigBackupName.Create(body.Name);
			if (!name.IsSuccess)
			{
				return PanelResults.Refuse(name.Failure!);
			}

			var restored = await handler.RollbackAsync(Instance(http), name.Value, ct).ConfigureAwait(false);
			if (restored.IsSuccess)
			{
				await audit.RecordAsync(http, "config_rollback", name.Value.Value, ct)
					.ConfigureAwait(false);
			}

			return PanelResults.From(restored);
		});

		routes.MapGet("/api/world-config", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			ServerConfigurationHandler handler,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadWorldTemplate), requireCsrf: false);

			return allowed.IsSuccess
				? PanelResults.From(await handler.ReadWorldTemplateAsync(Instance(http), ct).ConfigureAwait(false))
				: PanelResults.Refuse(allowed.Failure!);
		});

		routes.MapPut("/api/world-config", async (
			HttpContext http,
			JsonConfigRequest body,
			CommandAuthorizer authorizer,
			ServerConfigurationHandler handler,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeWorldTemplate));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var saved = await handler.SaveWorldTemplateAsync(Instance(http), body, ct).ConfigureAwait(false);
			if (saved.IsSuccess)
			{
				await audit.RecordAsync(http, "world_config_saved", string.Empty, ct)
					.ConfigureAwait(false);
			}

			return PanelResults.From(saved);
		});

		routes.MapGet("/api/world-config/runtime", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			ILiveWorldConfig live,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadLiveWorldConfig), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var world = await live.ReadAsync(Instance(http), ct).ConfigureAwait(false);

			return PanelResults.Json(new LiveWorldConfigResponse
			{
				Available = world.Available,
				Configuration = world.Configuration,
				Detail = world.Detail,
			});
		});

		routes.MapPost("/api/world-config/runtime", async (
			HttpContext http,
			LiveWorldConfigRequest body,
			CommandAuthorizer authorizer,
			ILiveWorldConfig live,
			AuditLog audit,
			EventBus events,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeLiveWorldConfig));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			// Любой отказ здесь — 409, включая неверное значение. Проверка в веб-процессе только
			// избавляет от лишнего вызова демона и отвечает тем же кодом.
			var command = WorldConfigCommand.Create(body.Action, body.DataType, body.Name, body.Value);
			if (!command.IsSuccess)
			{
				return PanelResults.Refuse(StatusCodes.Status409Conflict, command.Failure!.Message);
			}

			var sent = await live.ChangeAsync(Instance(http), command.Value, ct).ConfigureAwait(false);
			if (!sent.IsSuccess)
			{
				return PanelResults.Refuse(StatusCodes.Status409Conflict, sent.Failure!.Message);
			}

			await audit
				.RecordAsync(http, "runtime_world_config",
					$"{command.Value.Action}:{command.Value.Name}",
					ct)
				.ConfigureAwait(false);

			await events.EmitAsync(
					Instance(http),
					"world.config",
					Observability.Domain.EventSeverity.Info,
					"Команда настройки мира отправлена",
					$"{command.Value.Action}: {command.Value.Name}",
					"configuration",
					notify: false,
					ct: ct)
				.ConfigureAwait(false);

			return PanelResults.Json(new LiveWorldConfigChangeResponse
			{
				Action = command.Value.Action,
				DataType = command.Value.DataType,
				Name = command.Value.Name,
			});
		});
	}

	private static InstanceId Instance(HttpContext http) => PanelContext.Get(http)!.Instance;
}
