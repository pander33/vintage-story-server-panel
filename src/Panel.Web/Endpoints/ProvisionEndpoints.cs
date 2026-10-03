using System.Text.Json.Serialization;
using Panel.Contracts.Agent;
using Panel.Web.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Panel.Access.Authorization;
using Panel.Provisioning.Domain;
using Panel.Provisioning.Ports;
using Panel.SharedKernel;
using Panel.Web.Application;

namespace Panel.Web.Endpoints;

/// <summary>
/// Создание сервера из вкладки «Обзор»: состояние, проверка машины, запуск.
/// </summary>
public static class ProvisionEndpoints
{
	public static void MapProvision(this IEndpointRouteBuilder routes)
	{
		routes.MapGet("/api/provision", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			ProvisionService provision,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadProvision), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			return PanelResults.Json(await provision.SnapshotAsync(ct).ConfigureAwait(false));
		});

		// Ничего не меняет, но требует CSRF: проверка обращается к системе.
		routes.MapPost("/api/provision/preflight", async (
			HttpContext http,
			PreflightRequest body,
			CommandAuthorizer authorizer,
			ProvisionService provision,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ManageProvision));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			return PanelResults.Json(await provision.PreflightAsync(ChosenVersion.Create(body.Version), ct).ConfigureAwait(false));
		});

		routes.MapPost("/api/provision/create", async (
			HttpContext http,
			ServerRequest body,
			CommandAuthorizer authorizer,
			ProvisionService provision,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ManageProvision));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			// Обязательные поля ProvisionBody: без них FastAPI отвечал 422 ещё до маршрута.
			if (body.Id is null || body.Name is null || body.Version is null || body.ServerName is null)
			{
				return PanelResults.Refuse(StatusCodes.Status422UnprocessableEntity, "Не заполнены обязательные поля: id, name, version, server_name");
			}

			// Ошибка описания сервера — 409.
			var spec = ServerSpec.FromProvisionRequest(body.ToAnswers(ServerRequest.ProvisionDefaults));
			if (!spec.IsSuccess)
			{
				return PanelResults.Refuse(StatusCodes.Status409Conflict, spec.Failure!.Text);
			}

			var launched = await provision.LaunchAsync(spec.Value).ConfigureAwait(false);
			if (!launched.IsSuccess)
			{
				return PanelResults.Refuse(StatusCodes.Status409Conflict, launched.Failure!.Text);
			}

			await audit.RecordAsync(http, "instance_create_started", $"{spec.Value.Id.Value} {spec.Value.Version}", ct).ConfigureAwait(false);
			return PanelResults.Json(new LaunchResponse { Run = launched.Value.ToString() });
		});

		routes.MapPost("/api/provision/transfer", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IProvisioningHost host,
			ProvisionService provision,
			TransferPlans plans,
			StagingWriter staging,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ManageProvision));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			if (!string.Equals(http.Request.ContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
			{
				return PanelResults.Refuse(StatusCodes.Status415UnsupportedMediaType, "Ожидается application/octet-stream");
			}

			var limit = provision.MaxTransferBytes;
			if (http.Request.ContentLength > limit)
			{
				return PanelResults.Refuse(StatusCodes.Status413PayloadTooLarge, "Переносимый пакет превышает допустимый размер");
			}

			UploadLimits.RaiseBodyLimit(http, limit);

			var transfer = StagingWriter.Reserve(StagedFile.Purpose.Transfer, ".tar.gz");
			var retained = false;
			try
			{
				var written = await TransferUpload.WriteAsync(http.Request.Body, staging.PathOf(transfer), limit, ct).ConfigureAwait(false);
				if (!written.IsSuccess)
				{
					return PanelResults.Refuse(written.Failure!);
				}

				var inspection = await host.InspectTransferAsync(transfer, written.Value.Sha256, written.Value.Length, ct).ConfigureAwait(false);
				if (!inspection.IsSuccess)
				{
					return PanelResults.Refuse(inspection.Failure!);
				}

				var plan = new TransferPlan(transfer, written.Value.Sha256, written.Value.Length, inspection.Value.ActiveWorld, inspection.Value);
				var token = plans.Add(plan, http.Request.Cookies[PanelContextMiddleware.SessionCookie]!);
				retained = true;
				return PanelResults.Json(new TransferPlanResponse(token, written.Value.Sha256, written.Value.Length, inspection.Value.ActiveWorld, inspection.Value.GameVersion, inspection.Value.ManifestSha256));
			}
			finally
			{
				if (!retained)
				{
					staging.Discard(transfer);
				}
			}
		});

		routes.MapPost("/api/provision/import", async (
			HttpContext http,
			ImportRequest body,
			CommandAuthorizer authorizer,
			ProvisionService provision,
			TransferPlans plans,
			StagingWriter staging,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ManageProvision));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			if (body.Server.Id is null || body.Server.Name is null || body.Server.Version is null || body.Server.ServerName is null)
			{
				return PanelResults.Refuse(StatusCodes.Status422UnprocessableEntity, "Не заполнены обязательные поля: id, name, version, server_name");
			}

			var spec = ServerSpec.FromProvisionRequest(body.Server.ToAnswers(ServerRequest.ProvisionDefaults));
			if (!spec.IsSuccess)
			{
				return PanelResults.Refuse(spec.Failure!);
			}

			var confirmed = Confirmation.Exactly("ИМПОРТИРОВАТЬ " + spec.Value.Id.Value, "IMPORT " + spec.Value.Id.Value).Check(body.Confirmation);
			if (!confirmed.IsSuccess)
			{
				return PanelResults.Refuse(confirmed.Failure!);
			}

			var session = http.Request.Cookies[PanelContextMiddleware.SessionCookie]!;
			var plan = plans.Peek(body.Plan, session);
			if (provision.IsBusy || plan is null)
			{
				return PanelResults.Refuse(StatusCodes.Status409Conflict, "Создание уже идёт либо план импорта истёк или принадлежит другой сессии");
			}

			var versionRefused = TransferVersion.Check(plan.Inspection.GameVersion, spec.Value.Version, body.AcceptUpgrade);
			if (versionRefused is not null)
			{
				return PanelResults.Refuse(versionRefused);
			}

			var taken = plans.Take(body.Plan, http.Request.Cookies[PanelContextMiddleware.SessionCookie]!);
			if (!taken.IsSuccess)
			{
				return PanelResults.Refuse(taken.Failure!);
			}

			var launched = await provision.LaunchAsync(spec.Value, taken.Value).ConfigureAwait(false);
			if (!launched.IsSuccess)
			{
				staging.Discard(taken.Value.File);
				return PanelResults.Refuse(launched.Failure!);
			}

			await audit.RecordAsync(http, "instance_import_started", $"{spec.Value.Id.Value} {taken.Value.Sha256}", ct).ConfigureAwait(false);
			return PanelResults.Json(new LaunchResponse { Run = launched.Value.ToString() });
		});

		// Каталог новых серверов — маршруты 2.0.
		routes.MapGet("/api/settings/servers-root", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IProvisioningHost host,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ReadProvision), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var disk = await host.DiskFreeAsync(ct).ConfigureAwait(false);
			return disk.IsSuccess
				? PanelResults.Json(new ServersRootResponse
				{
					InstancesRoot = disk.Value.InstancesRoot,
					Free = disk.Value.Free,
					Total = disk.Value.Total,
				})
				: PanelResults.Refuse(disk.Failure!);
		});

		routes.MapPut("/api/settings/servers-root", async (
			HttpContext http,
			ServersRootRequest body,
			CommandAuthorizer authorizer,
			IProvisioningHost host,
			ProvisionService provision,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ChangeServersRoot));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			// Идущее создание уже выбрало каталог, и смена во время него запутала бы оператора.
			if (provision.IsBusy)
			{
				return PanelResults.Refuse(StatusCodes.Status409Conflict, "Идёт создание сервера: каталог серверов можно сменить после его завершения");
			}

			var path = (body.Path ?? string.Empty).Trim();
			if (path.Length is 0 or > 240)
			{
				return PanelResults.Refuse(StatusCodes.Status400BadRequest, "Укажите каталог для серверов: полный путь не длиннее 240 символов");
			}

			var changed = await host.SetInstancesRootAsync(path, ct).ConfigureAwait(false);
			if (!changed.IsSuccess)
			{
				return PanelResults.Refuse(changed.Failure!);
			}

			await audit.RecordAsync(http, "servers_root", changed.Value.InstancesRoot, ct).ConfigureAwait(false);
			var disk = await host.DiskFreeAsync(ct).ConfigureAwait(false);
			return PanelResults.Json(new ServersRootResponse
			{
				InstancesRoot = changed.Value.InstancesRoot,
				Previous = changed.Value.Previous,
				Free = disk.IsSuccess ? disk.Value.Free : 0,
				Total = disk.IsSuccess ? disk.Value.Total : 0,
			});
		});
	}
}

/// <summary>Ответ проверки переносимого пакета.</summary>
public sealed record TransferPlanResponse(
	[property: JsonPropertyName("plan")] string Plan,
	[property: JsonPropertyName("sha256")] string Sha256,
	[property: JsonPropertyName("length")] long Length,
	[property: JsonPropertyName("active_world")] string ActiveWorld,
	[property: JsonPropertyName("game_version")] string GameVersion,
	[property: JsonPropertyName("manifest_sha256")] string ManifestSha256)
{
	[JsonPropertyName("ok")] public bool Ok => true;
}

/// <summary>Подтверждённый запрос импорта ранее проверенного пакета.</summary>
public sealed record ImportRequest
{
	[JsonPropertyName("plan")] public string? Plan { get; init; }
	[JsonPropertyName("confirmation")] public string Confirmation { get; init; } = string.Empty;
	[JsonPropertyName("server")] public ServerRequest Server { get; init; } = new();

	/// <summary>Согласие на необратимое обновление мира, если выбранная версия игры новее версии в пакете.</summary>
	[JsonPropertyName("accept_upgrade")] public bool AcceptUpgrade { get; init; }
}

/// <summary>Тело проверки машины.</summary>
public sealed record PreflightRequest
{
	[JsonPropertyName("version")] public string Version { get; init; } = string.Empty;
}

/// <summary>
/// Описание сервера в теле запроса. У мастера (<c>ServerBody</c>) у всех полей есть
/// умолчания; у <c>/api/provision/create</c> (<c>ProvisionBody</c>) четыре поля обязательны,
/// умолчания остальных те же.
/// </summary>
public sealed record ServerRequest
{
	public static readonly ServerAnswers ProvisionDefaults = new()
	{
		Port = ServerSpec.DefaultPort,
		MaxPlayers = 16,
		Pvp = true,
		Language = "ru",
		WorldName = ServerSpec.DefaultWorldName,
		Playstyle = "surviveandbuild",
	};

	public static readonly ServerAnswers WizardDefaults = ProvisionDefaults with { Id = "main" };

	[JsonPropertyName("id")] public string? Id { get; init; }
	[JsonPropertyName("name")] public string? Name { get; init; }
	[JsonPropertyName("version")] public string? Version { get; init; }
	[JsonPropertyName("port")] public long? Port { get; init; }
	[JsonPropertyName("server_name")] public string? ServerName { get; init; }
	[JsonPropertyName("description")] public string? Description { get; init; }
	[JsonPropertyName("max_players")] public long? MaxPlayers { get; init; }
	[JsonPropertyName("whitelist")] public bool? Whitelist { get; init; }
	[JsonPropertyName("pvp")] public bool? Pvp { get; init; }
	[JsonPropertyName("language")] public string? Language { get; init; }
	[JsonPropertyName("world_name")] public string? WorldName { get; init; }
	[JsonPropertyName("seed")] public string? Seed { get; init; }
	[JsonPropertyName("playstyle")] public string? Playstyle { get; init; }

	public ServerAnswers ToAnswers(ServerAnswers defaults) => new()
	{
		Id = Id ?? defaults.Id,
		Name = Name ?? defaults.Name,
		Version = Version ?? defaults.Version,
		Port = Port ?? defaults.Port,
		ServerName = ServerName ?? defaults.ServerName,
		Description = Description ?? defaults.Description,
		MaxPlayers = MaxPlayers ?? defaults.MaxPlayers,
		Whitelist = Whitelist ?? defaults.Whitelist,
		Pvp = Pvp ?? defaults.Pvp,
		Language = Language ?? defaults.Language,
		WorldName = WorldName ?? defaults.WorldName,
		Seed = Seed ?? defaults.Seed,
		Playstyle = Playstyle ?? defaults.Playstyle,
	};
}

/// <summary>Тело смены каталога серверов.</summary>
public sealed record ServersRootRequest
{
	[JsonPropertyName("path")] public string? Path { get; init; }
}

/// <summary>Каталог новых серверов и место на его диске.</summary>
public sealed record ServersRootResponse
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;

	[JsonPropertyName("instances_root")] public string InstancesRoot { get; init; } = string.Empty;

	/// <summary>Прежний каталог; только в ответе на смену.</summary>
	[JsonPropertyName("previous")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Previous { get; init; }

	[JsonPropertyName("free")] public long Free { get; init; }

	[JsonPropertyName("total")] public long Total { get; init; }
}

/// <summary>Ответ запуска создания: <c>{"ok": true, "run": …}</c>.</summary>
public sealed record LaunchResponse
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;

	[JsonPropertyName("run")] public string Run { get; init; } = string.Empty;
}
