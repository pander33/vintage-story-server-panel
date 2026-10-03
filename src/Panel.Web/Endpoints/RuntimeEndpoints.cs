using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Panel.Access.Authorization;
using Panel.Configuration.Ports;
using Panel.Contracts.Http;
using Panel.Contracts.Privacy;
using Panel.Contracts.ServerCommands;
using Panel.Runtime.Domain;
using Panel.Runtime.Ports;
using Panel.SharedKernel;
using Panel.Web.Application;
using Panel.Web.Middleware;

namespace Panel.Web.Endpoints;

/// <summary>Управление службой, журнал и консоль.</summary>
public static class RuntimeEndpoints
{
	public static void MapRuntime(this IEndpointRouteBuilder routes)
	{
		routes.MapPost("/api/service", async (
			HttpContext http,
			ServiceActionRequest body,
			CommandAuthorizer authorizer,
			ServerControlHandler handler,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ControlService));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var action = ParseAction(body.Action);
			if (!action.IsSuccess)
			{
				return PanelResults.Refuse(action.Failure!);
			}

			var instance = PanelContext.Get(http)!.Instance;
			var result = await handler.OperatorServiceAsync(instance, action.Value, ct).ConfigureAwait(false);
			if (!result.IsSuccess)
			{
				return PanelResults.Refuse(result.Failure!);
			}

			await audit.RecordAsync(http, "service", body.Action, ct)
				.ConfigureAwait(false);

			return PanelResults.Json(new ServiceActionResponse { Action = body.Action });
		});

		// Виден ли игровой порт из интернета. Проверять имеет смысл только работающий сервер: порт
		// остановленного никто не слушает, и ответ «закрыт» ничего бы не значил.
		routes.MapPost("/api/reachability", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IServerRuntime runtime,
			IServerConfigStore configs,
			PortReachability reachability,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(CheckReachability));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var instance = PanelContext.Get(http)!.Instance;
			var status = await runtime.StatusAsync(instance, ct).ConfigureAwait(false);
			if (!status.IsSuccess)
			{
				return PanelResults.Refuse(status.Failure!);
			}

			if (!status.Value.IsActive)
			{
				return PanelResults.Refuse(Failure.Conflict("Сервер остановлен, и порт никто не слушает. Запустите сервер и проверьте снова"));
			}

			if (await GameEndpoint.ReadAsync(configs, instance, ct).ConfigureAwait(false) is not { } endpoint)
			{
				return PanelResults.Refuse(Failure.Unavailable("Не удалось прочитать порт из serverconfig.json"));
			}

			var checkedPort = await reachability.CheckAsync(endpoint.Port, ct).ConfigureAwait(false);
			if (!checkedPort.IsSuccess)
			{
				return PanelResults.Refuse(checkedPort.Failure!);
			}

			var result = checkedPort.Value;
			return PanelResults.Json(new JsonObject
			{
				["public_address"] = result.PublicAddress,
				["port"] = result.Port,
				["protocol"] = "tcp",
				["reachable"] = result.Reachable,
				["checked_at"] = result.CheckedAt.ToUnixTimeSeconds(),
				["checker"] = result.Checker,
			});
		});

		routes.MapGet("/api/server/actions", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			ServerControlHandler handler,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(
				http, authorizer, typeof(ReadCommandCatalogue), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var server = await handler.LoadAsync(PanelContext.Get(http)!.Instance, ct).ConfigureAwait(false);
			var console = server.IsSuccess
				? new ConsoleStateResponse
				{
					Transport = ConsoleCapability.ToWire(server.Value.Console.Transport),
					Available = server.Value.Console.IsAvailable,
					Detail = server.Value.Console.UnavailableReason,
				}

				// Каталог отдаётся и тогда, когда сервер недоступен: интерфейс должен показать, что
				// именно недоступно.
				: new ConsoleStateResponse
				{
					Transport = "disabled",
					Available = false,
					Detail = server.Failure!.Text,
				};

			return PanelResults.Json(new CommandCatalogueResponse
			{
				Commands = [.. ServerCommandCatalogue.All.Select(ServerCommandView.Of)],
				GameModes = ServerCommandCatalogue.GameModes,
				Console = console,
				Capability = new CommandCapabilityResponse
				{
					Commands = console.Available,
					Transport = console.Transport,
					Detail = console.Detail ?? Phrase.From(string.Empty),
				},
			});
		});

		routes.MapPost("/api/server/action", async (
			HttpContext http,
			ServerCommandRequest body,
			CommandAuthorizer authorizer,
			ServerControlHandler handler,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(SendServerCommand));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var instance = PanelContext.Get(http)!.Instance;
			var echo = await handler.SendAsync(instance, body, ct).ConfigureAwait(false);
			if (!echo.IsSuccess)
			{
				return PanelResults.Refuse(echo.Failure!);
			}

			await audit.RecordAsync(http, "server_command", $"{body.Action} {body.Value}".Trim(), ct)
				.ConfigureAwait(false);

			var spec = ServerCommandCatalogue.Find(body.Action);
			return PanelResults.Json(new ServerCommandResponse
			{
				Action = body.Action,
				Command = spec.IsSuccess ? spec.Value.Render(body.Value).Match(line => line, _ => string.Empty) : string.Empty,
				Reply = [.. echo.Value.Lines.Select(line => AddressMask.Scrub(line, allowed.Value.HidesAddresses))],
			});
		});

		// Маршрут 2.0. Строка проверяется до вызова демона и самим демоном и пишется в
		// аудит целиком: иначе не останется следа того, что администратор отправил серверу.
		routes.MapPost("/api/server/console", async (
			HttpContext http,
			RawConsoleRequest body,
			CommandAuthorizer authorizer,
			ServerControlHandler handler,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(SendRawConsole));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var line = RawConsoleLine.Create(body.Command);
			if (!line.IsSuccess)
			{
				return PanelResults.Refuse(line.Failure!);
			}

			var echo = await handler.SendRawAsync(PanelContext.Get(http)!.Instance, line.Value, ct).ConfigureAwait(false);
			if (!echo.IsSuccess)
			{
				return PanelResults.Refuse(echo.Failure!);
			}

			await audit.RecordAsync(http, "server_console", line.Value.Text, ct).ConfigureAwait(false);

			return PanelResults.Json(new RawConsoleResponse
			{
				Command = line.Value.Text,
				Reply = [.. echo.Value.Lines.Select(reply => AddressMask.Scrub(reply, allowed.Value.HidesAddresses))],
			});
		});
	}

	private static Result<ServiceAction> ParseAction(string raw) => raw switch
	{
		"start" => ServiceAction.Start,
		"stop" => ServiceAction.Stop,
		"restart" => ServiceAction.Restart,
		_ => Failure.Invalid("Недопустимое действие"),
	};
}
