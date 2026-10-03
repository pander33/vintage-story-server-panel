using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Panel.Access.Authorization;
using Panel.Access.Domain;
using Panel.Access.Passwords;
using Panel.Access.Ports;
using Panel.Access.Sessions;
using Panel.Catalogues.Infrastructure;
using Panel.Contracts;
using Panel.Contracts.Agent;
using Panel.Fleet.Ports;
using Panel.Provisioning.Domain;
using Panel.Provisioning.Ports;
using Panel.SharedKernel;
using Panel.Web.Application;
using Panel.Web.Middleware;

namespace Panel.Web.Endpoints;

/// <summary>
/// Мастер установки, от свежей установки до работающего сервера.
/// </summary>
/// <remarks>
/// <para>
/// Пока у панели нет администратора и сервера, вкладкам нечего показывать, и весь интерфейс —
/// одна страница мастера. Первый шаг защищён одноразовым токеном от установщика, чтобы
/// администратором не стал первый, кто открыл порт. После захвата мастер работает в обычной
/// сессии администратора.
/// </para>
/// <para>
/// Завершённый мастер навсегда отвечает 410: это записано в <c>setup.json</c>.
/// </para>
/// </remarks>
public static partial class SetupEndpoints
{
	public static void MapSetup(this IEndpointRouteBuilder routes)
	{
		// Анонимный маршрут отдаёт только то, что нужно для отрисовки нужного экрана.
		routes.MapGet("/api/setup/state", async (SetupService setup, CancellationToken ct) =>
		{
			var journey = await setup.CurrentAsync(ct).ConfigureAwait(false);
			var admin = await setup.HasAdministratorAsync(ct).ConfigureAwait(false);
			if (!admin.IsSuccess)
			{
				return PanelResults.Refuse(admin.Failure!);
			}

			return PanelResults.Json(new JsonObject
			{
				["required"] = !journey.IsCompleted,
				["step"] = SetupStepWire.ToWire(journey.Step),
				["needs_token"] = !admin.Value,
				["token_present"] = setup.ReadToken() is not null,
				["completed_at"] = journey.CompletedAt,
			});
		});

		routes.MapPost("/api/setup/claim", ClaimAsync);

		routes.MapGet("/api/setup/summary", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			SetupService setup,
			IRegistryAdministration registry,
			ProvisionService provision,
			PanelOptions options,
			CancellationToken ct) =>
		{
			var guarded = await GuardAsync(http, authorizer, setup, csrf: false, ct).ConfigureAwait(false);
			if (guarded.Refusal is { } refusal)
			{
				return refusal;
			}

			var listed = await registry.ListAsync(ct).ConfigureAwait(false);
			var instances = listed.IsSuccess ? listed.Value.Instances : [];
			var (id, port) = Suggestion.For(instances.Select(item => (item.Id, item.Port)));

			return PanelResults.Json(new JsonObject
			{
				["step"] = SetupStepWire.ToWire(guarded.Journey!.Step),
				["draft"] = guarded.Journey.Drafts,
				["instances"] = JsonSerializer.SerializeToNode(instances, PanelJson.Options),
				["writable"] = listed.IsSuccess && listed.Value.Writable,
				["suggest"] = new JsonObject { ["id"] = id, ["port"] = port },
				["languages"] = new JsonArray([.. ServerSpec.Languages.Select(item => (JsonNode?) item)]),
				["playstyles"] = new JsonArray([.. ServerSpec.Playstyles.Select(item => (JsonNode?) item)]),
				["access"] = new JsonObject
				{
					["bind"] = options.Bind,
					["port"] = options.Port,
					["allowed_hosts"] = string.Join(",", options.AllowedHosts),
					["secure_cookie"] = options.SecureCookie,
				},
				["provision"] = await provision.SnapshotAsync(ct).ConfigureAwait(false),
			});
		});

		routes.MapPost("/api/setup/access", async (
			HttpContext http,
			AccessRequest body,
			CommandAuthorizer authorizer,
			SetupService setup,
			IProvisioningHost host,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var guarded = await GuardAsync(http, authorizer, setup, csrf: true, ct).ConfigureAwait(false);
			if (guarded.Refusal is { } refusal)
			{
				return refusal;
			}

			var access = AccessSettings.Create(body.Bind, body.Port, body.AllowedHosts, body.SecureCookie);
			if (!access.IsSuccess)
			{
				return PanelResults.Refuse(access.Failure!);
			}

			if (guarded.Journey!.Step < SetupStep.Access)
			{
				return PanelResults.Refuse(StatusCodes.Status409Conflict, "Сначала пройдите предыдущий шаг мастера установки");
			}

			Phrase? firewallWarning = null;
			foreach (var (key, value) in access.Value.Values)
			{
				// Значение проверяется и здесь, чтобы оператор получил понятное «неверный адрес», а не отказ
				// демона.
				var setting = PanelSetting.Create(key, value);
				if (!setting.IsSuccess)
				{
					return PanelResults.Refuse(setting.Failure!);
				}

				var written = await host.SetPanelEnvironmentAsync(setting.Value, ct).ConfigureAwait(false);
				if (!written.IsSuccess)
				{
					return Upstream(written.Failure!);
				}

				firewallWarning = written.Value.FirewallWarning ?? firewallWarning;
			}

			var remembered = await setup.ChangeAsync(journey => journey.RememberAccess(access.Value), ct).ConfigureAwait(false);
			if (!remembered.IsSuccess)
			{
				return PanelResults.Refuse(remembered.Failure!);
			}

			await audit.RecordAsync(http, "setup_access", $"{access.Value.Bind}:{access.Value.Port.ToString(CultureInfo.InvariantCulture)}", ct).ConfigureAwait(false);
			return PanelResults.Json(new JsonObject
			{
				["ok"] = true,
				["published"] = access.Value.Published,
				["restart_required"] = true,
				["url"] = PanelAddress.After(access.Value.Bind, access.Value.Port.ToString(CultureInfo.InvariantCulture), http.Request.Host.HasValue ? http.Request.Host.Host : null),
				["firewall_warning"] = firewallWarning is null ? null : PhraseJsonConverter.ToNode(firewallWarning),
			});
		});

		routes.MapGet("/api/setup/catalogue", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			SetupService setup,
			VersionCatalogue catalogue,
			CancellationToken ct,
			string? refresh = null) =>
		{
			var guarded = await GuardAsync(http, authorizer, setup, csrf: false, ct).ConfigureAwait(false);
			if (guarded.Refusal is { } refusal)
			{
				return refusal;
			}

			var snapshot = await catalogue.SnapshotAsync(Flag(refresh), ct).ConfigureAwait(false);
			return snapshot.IsSuccess ? PanelResults.Json(snapshot.Value.ToJson()) : Upstream(snapshot.Failure!);
		});

		routes.MapPost("/api/setup/preflight", async (
			HttpContext http,
			PreflightRequest body,
			CommandAuthorizer authorizer,
			SetupService setup,
			ProvisionService provision,
			CancellationToken ct) =>
		{
			var guarded = await GuardAsync(http, authorizer, setup, csrf: true, ct).ConfigureAwait(false);
			if (guarded.Refusal is { } refusal)
			{
				return refusal;
			}

			var version = ChosenVersion.Create(body.Version);
			var required = version is null ? SetupStep.Preflight : SetupStep.Version;
			if (guarded.Journey!.Step < required)
			{
				return PanelResults.Refuse(StatusCodes.Status409Conflict, "Сначала пройдите предыдущий шаг мастера установки");
			}

			var report = await provision.PreflightAsync(version, ct).ConfigureAwait(false);

			// Проваленная проверка не двигает мастер вперёд: следующий экран описывал бы состояние,
			// которого на машине нет.
			if (report["ok"] is JsonValue ok && ok.GetValue<bool>())
			{
				var remembered = await setup
					.ChangeAsync(journey => version is { } chosen ? journey.RememberVersion(chosen) : journey.RememberMachineCheck(), ct)
					.ConfigureAwait(false);
				if (!remembered.IsSuccess)
				{
					return PanelResults.Refuse(remembered.Failure!);
				}
			}

			return PanelResults.Json(report);
		});

		// Здесь ничего не создаётся, ответы только проверяются и записываются.
		routes.MapPost("/api/setup/server", async (
			HttpContext http,
			ServerRequest body,
			CommandAuthorizer authorizer,
			SetupService setup,
			IRegistryAdministration registry,
			CancellationToken ct) =>
		{
			var guarded = await GuardAsync(http, authorizer, setup, csrf: true, ct).ConfigureAwait(false);
			if (guarded.Refusal is { } refusal)
			{
				return refusal;
			}

			var spec = ServerSpec.FromWizard(body.ToAnswers(ServerRequest.WizardDefaults));
			if (!spec.IsSuccess)
			{
				return PanelResults.Refuse(spec.Failure!);
			}

			var listed = await registry.ListAsync(ct).ConfigureAwait(false);
			foreach (var item in listed.IsSuccess ? listed.Value.Instances : [])
			{
				if (item.Id == spec.Value.Id.Value)
				{
					return PanelResults.Refuse(StatusCodes.Status409Conflict, $"Сервер с идентификатором {spec.Value.Id.Value} уже существует");
				}

				if (item.Port == spec.Value.Port)
				{
					return PanelResults.Refuse(StatusCodes.Status409Conflict, $"Порт {spec.Value.Port.ToString(CultureInfo.InvariantCulture)} уже занят сервером {item.Id}");
				}
			}

			var remembered = await setup.ChangeAsync(journey => journey.RememberServer(spec.Value), ct).ConfigureAwait(false);
			if (!remembered.IsSuccess)
			{
				return PanelResults.Refuse(remembered.Failure!);
			}

			return PanelResults.Json(new JsonObject { ["ok"] = true, ["spec"] = spec.Value.ToJson() });
		});

		routes.MapPost("/api/setup/create", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			SetupService setup,
			ProvisionService provision,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var guarded = await GuardAsync(http, authorizer, setup, csrf: true, ct).ConfigureAwait(false);
			if (guarded.Refusal is { } refusal)
			{
				return refusal;
			}

			var spec = guarded.Journey!.SpecificationToCreate();
			if (!spec.IsSuccess)
			{
				return PanelResults.Refuse(StatusCodes.Status400BadRequest, spec.Failure!.Text);
			}

			var launched = await provision.LaunchAsync(spec.Value).ConfigureAwait(false);
			if (!launched.IsSuccess)
			{
				return PanelResults.Refuse(StatusCodes.Status409Conflict, launched.Failure!.Text);
			}

			await setup.ChangeAsync(journey => journey.BeginCreation(launched.Value.Value), ct).ConfigureAwait(false);
			await audit.RecordAsync(http, "setup_create_started", $"{spec.Value.Id.Value} {spec.Value.Version}", ct).ConfigureAwait(false);
			return PanelResults.Json(new LaunchResponse { Run = launched.Value.ToString() });
		});

		routes.MapGet("/api/setup/stream", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			SetupService setup,
			ProvisionService provision,
			CancellationToken ct) =>
		{
			var guarded = await GuardAsync(http, authorizer, setup, csrf: false, ct).ConfigureAwait(false);
			if (guarded.Refusal is { } refusal)
			{
				return refusal;
			}

			await ServerSentEvents.StreamAsync(http, TimeSpan.FromSeconds(2), ct, async previous =>
			{
				var encoded = LocalizedJson.Serialize(await provision.SnapshotAsync(ct).ConfigureAwait(false), http);
				return encoded == previous ? null : encoded;
			}).ConfigureAwait(false);
			return Results.Empty;
		});

		routes.MapPost("/api/setup/finish", FinishAsync);
	}

	/// <summary>
	/// Захват: при верном токене создаётся администратор, токен удаляется, выдаётся сессия.
	/// Порядок проверок: 410, 409, 429, 503, 403, 400.
	/// </summary>
	private static async Task<IResult> ClaimAsync(
		HttpContext http,
		ClaimRequest body,
		SetupService setup,
		IAccountStore accounts,
		IRegistryStore registry,
		SessionWriter sessions,
		AuditLog audit,
		PanelOptions options,
		TimeProvider clock,
		CancellationToken ct)
	{
		var journey = await setup.CurrentAsync(ct).ConfigureAwait(false);
		if (journey.IsCompleted)
		{
			return Gone();
		}

		var directory = await accounts.LoadAsync(ct).ConfigureAwait(false);
		if (!directory.IsSuccess)
		{
			return PanelResults.Refuse(directory.Failure!);
		}

		if (!directory.Value.IsEmpty)
		{
			return PanelResults.Refuse(StatusCodes.Status409Conflict, "Администратор уже создан, войдите обычным способом");
		}

		if (setup.ClaimThrottle.IsBlocked(SetupService.ClaimKey))
		{
			return PanelResults.Refuse(StatusCodes.Status429TooManyRequests, "Слишком много попыток. Подождите пять минут");
		}

		var token = setup.ReadToken();
		if (token is null)
		{
			return PanelResults.Refuse(StatusCodes.Status503ServiceUnavailable, "Токен захвата не найден. Выполните на сервере: sudo vs-panel-claim");
		}

		if (!token.Accepts(OfferedToken.Create(body.Token)))
		{
			setup.ClaimThrottle.RecordFailure(SetupService.ClaimKey);
			await audit.RecordAsync(http, "setup_claim_failed", string.Empty, ct).ConfigureAwait(false);
			return PanelResults.Refuse(StatusCodes.Status403Forbidden, "Неверный токен захвата");
		}

		// Токен расходуется только при полном успехе: короткий пароль отвергается раньше.
		var requested = body.Username.Trim() is { Length: > 0 } given ? given : "admin";
		var name = UserName.Create(requested);
		if (!name.IsSuccess)
		{
			return PanelResults.Refuse(name.Failure!);
		}

		var password = PasswordHash.Create(body.Password);
		if (!password.IsSuccess)
		{
			return PanelResults.Refuse(password.Failure!);
		}

		var updated = directory.Value.Put(name.Value, password.Value, Role.Admin);
		if (!updated.IsSuccess)
		{
			return PanelResults.Refuse(updated.Failure!);
		}

		var saved = await accounts.SaveAsync(updated.Value, ct).ConfigureAwait(false);
		if (!saved.IsSuccess)
		{
			return PanelResults.Refuse(saved.Failure!);
		}

		setup.ConsumeToken();

		// Если установщик уже создал сервер, спрашивать больше нечего, и мастер закрывается вместе
		// с появлением администратора.
		var snapshot = await registry.LoadAsync(ct).ConfigureAwait(false);
		var claimed = await setup
			.ChangeAsync(current => current.Claim(SetupService.Registered(snapshot), clock.GetUtcNow().ToUnixTimeSeconds()), ct)
			.ConfigureAwait(false);
		var completed = claimed.IsSuccess && claimed.Value;

		var account = updated.Value.Find(name.Value)!;
		var issued = sessions.Issue(new AuthenticatedUser(account.Name, account.Role, account.Generation), options.SessionLifetime);
		http.Response.Cookies.Append(PanelContextMiddleware.SessionCookie, issued.Token, new CookieOptions
		{
			HttpOnly = true,
			Secure = options.SecureCookie,
			SameSite = SameSiteMode.Strict,
			MaxAge = options.SessionLifetime,
			Path = "/",
		});

		await audit.RecordAsync(http, "setup_claimed", account.Name.Value, ct).ConfigureAwait(false);
		return PanelResults.Json(new JsonObject
		{
			["ok"] = true,
			["csrf"] = issued.CsrfToken,
			["completed"] = completed,
			["user"] = account.Name.Value,
			["role"] = "admin",
			["generation"] = account.Generation,
		});
	}

	/// <summary>
	/// Направить панель на созданный сервер и закрыть мастер. <c>setup.json</c> записывается до
	/// запроса перезапуска, чтобы перезапущенная панель не застала мастер открытым.
	/// Неудачный перезапуск возвращается в ответе.
	/// </summary>
	/// <remarks>
	/// <c>without_server</c> закрывает мастер без сервера: окружение панели не меняется, кроме
	/// доступа, записанного на своём шаге. Если идёт создание, ответ 409, как для любого действия
	/// при занятой панели.
	/// </remarks>
	private static async Task<IResult> FinishAsync(
		HttpContext http,
		FinishRequest body,
		CommandAuthorizer authorizer,
		SetupService setup,
		IRegistryStore registry,
		IProvisioningHost host,
		ProvisionService provision,
		AuditLog audit,
		PanelOptions options,
		TimeProvider clock,
		ILoggerFactory logs,
		CancellationToken ct)
	{
		var guarded = await GuardAsync(http, authorizer, setup, csrf: true, ct).ConfigureAwait(false);
		if (guarded.Refusal is { } refusal)
		{
			return refusal;
		}

		if (body.WithoutServer)
		{
			if (provision.IsBusy)
			{
				return PanelResults.Refuse(StatusCodes.Status409Conflict, "Сервер ещё создаётся: дождитесь окончания");
			}

			var closed = await setup
				.ChangeAsync(journey => journey.FinishWithoutServer(clock.GetUtcNow().ToUnixTimeSeconds()), ct)
				.ConfigureAwait(false);
			if (!closed.IsSuccess)
			{
				return PanelResults.Refuse(closed.Failure!);
			}

			await audit.RecordAsync(http, "setup_finished", "without_server", ct).ConfigureAwait(false);
			return await RestartAsync(host, body, null, PanelUrl(http, guarded.Journey!, options), ct).ConfigureAwait(false);
		}

		var draft = guarded.Journey!.Draft(SetupStep.Server);
		var instanceText = draft["id"] is JsonValue id && id.TryGetValue<string>(out var text) ? text : string.Empty;
		var registered = SetupService.Registered(await registry.LoadAsync(ct).ConfigureAwait(false));
		var instance = InstanceId.Create(instanceText);
		if (!instance.IsSuccess || instance.Value.IsRegistry || !registered.Contains(instance.Value))
		{
			return PanelResults.Refuse(StatusCodes.Status409Conflict, "Сервер ещё не зарегистрирован: дождитесь окончания создания");
		}

		var pointed = PanelSetting.Create("PANEL_INSTANCE", instance.Value.Value);
		var written = pointed.IsSuccess
			? await host.SetPanelEnvironmentAsync(pointed.Value, ct).ConfigureAwait(false)
			: Result.Fail<PanelEnvironmentSetPayload>(pointed.Failure!);
		if (!written.IsSuccess)
		{
			return Upstream(written.Failure!);
		}

		// Совместимость модов проверяется по этой версии; с прежней моды подбирались бы под другую
		// игру. Отказ — предупреждение, а не ошибка.
		var version = draft["version"] is JsonValue declared && declared.TryGetValue<string>(out var versionText) ? versionText : string.Empty;
		var gameVersion = PanelSetting.Create("PANEL_GAME_VERSION", version);
		var versionWritten = gameVersion.IsSuccess
			? await host.SetPanelEnvironmentAsync(gameVersion.Value, ct).ConfigureAwait(false)
			: Result.Fail<PanelEnvironmentSetPayload>(gameVersion.Failure!);
		if (!versionWritten.IsSuccess)
		{
			GameVersionNotWritten(logs.CreateLogger("Panel.Web.Setup"), versionWritten.Failure!.Message);
		}

		var url = PanelUrl(http, guarded.Journey, options);

		var finished = await setup
			.ChangeAsync(journey => journey.Finish(instance.Value, registered, clock.GetUtcNow().ToUnixTimeSeconds()), ct)
			.ConfigureAwait(false);
		if (!finished.IsSuccess)
		{
			return PanelResults.Refuse(finished.Failure!);
		}

		await audit.RecordAsync(http, "setup_finished", instance.Value.Value, ct).ConfigureAwait(false);
		return await RestartAsync(host, body, instance.Value.Value, url, ct).ConfigureAwait(false);
	}

	/// <summary>Адрес панели после перезапуска: из ответа шага «Доступ», иначе текущий.</summary>
	private static string PanelUrl(HttpContext http, SetupJourney journey, PanelOptions options)
	{
		var accessDraft = journey.Draft(SetupStep.Access);
		var bind = accessDraft["PANEL_BIND"] is JsonValue b && b.TryGetValue<string>(out var bindText) ? bindText : options.Bind;
		var port = accessDraft["PANEL_PORT"] is JsonValue p && p.TryGetValue<string>(out var portText)
			? portText
			: options.Port.ToString(CultureInfo.InvariantCulture);
		return PanelAddress.After(bind, port, http.Request.Host.HasValue ? http.Request.Host.Host : null);
	}

	/// <summary>Мастер уже закрыт на диске: перезапуск панели и ответ о нём. Без сервера <c>instance</c> пуст.</summary>
	private static async Task<IResult> RestartAsync(IProvisioningHost host, FinishRequest body, string? instance, string url, CancellationToken ct)
	{
		var restarted = false;
		if (body.Restart)
		{
			var restart = await host.RestartPanelAsync(ct).ConfigureAwait(false);
			if (!restart.IsSuccess)
			{
				return PanelResults.Json(new JsonObject
				{
					["ok"] = true,
					["instance"] = instance,
					["url"] = url,
					["restarted"] = false,
					["detail"] = PhraseJsonConverter.ToNode(Phrase.Format($"Панель не удалось перезапустить автоматически: {restart.Failure!}")),
				});
			}

			restarted = true;
		}

		return PanelResults.Json(new JsonObject
		{
			["ok"] = true,
			["instance"] = instance,
			["url"] = url,
			["restarted"] = restarted,
			["detail"] = PhraseJsonConverter.ToNode(restarted ? Phrase.From("Перезапуск панели выполняется") : Phrase.From("Перезапустите панель: sudo systemctl restart vs-panel")),
		});
	}

	/// <summary>
	/// <c>guard()</c> и <c>identify()</c>/<c>admin()</c>: завершённый мастер — 410, до захвата —
	/// 403, после — сессия администратора (с CSRF для изменений).
	/// </summary>
	private static async Task<(IResult? Refusal, SetupJourney? Journey)> GuardAsync(
		HttpContext http,
		CommandAuthorizer authorizer,
		SetupService setup,
		bool csrf,
		CancellationToken ct)
	{
		var journey = await setup.CurrentAsync(ct).ConfigureAwait(false);
		if (journey.IsCompleted)
		{
			return (Gone(), null);
		}

		if (csrf)
		{
			var admin = await setup.HasAdministratorAsync(ct).ConfigureAwait(false);
			if (!admin.IsSuccess)
			{
				return (PanelResults.Refuse(admin.Failure!), null);
			}

			if (!admin.Value)
			{
				return (PanelResults.Refuse(StatusCodes.Status403Forbidden, "Сначала завершите первый шаг: захват панели"), null);
			}
		}

		var allowed = PanelResults.Authorize(http, authorizer, typeof(RunSetupWizard), requireCsrf: csrf);
		if (!allowed.IsSuccess)
		{
			return (PanelResults.Refuse(allowed.Failure!), null);
		}

		// Администратор есть, а указатель на захвате: учётная запись появилась раньше мастера.
		if (journey.Step == SetupStep.Claim)
		{
			var moved = await setup.ChangeAsync(current => Result.Ok(current.AdministratorPresent()), ct).ConfigureAwait(false);
			if (moved.IsSuccess)
			{
				journey = await setup.CurrentAsync(ct).ConfigureAwait(false);
			}
		}

		return (null, journey);
	}

	private static IResult Gone() => PanelResults.Refuse(StatusCodes.Status410Gone, "Мастер установки уже завершён");

	/// <summary>Отказ привилегированной стороны — 502.</summary>
	private static IResult Upstream(Failure failure) => PanelResults.Refuse(StatusCodes.Status502BadGateway, failure.Text);

	private static bool Flag(string? value) =>
		value?.Trim().ToLowerInvariant() is "1" or "true" or "on" or "yes" or "y" or "t";

	[LoggerMessage(Level = LogLevel.Warning, Message = "Версия игры в окружении панели не обновлена: {Detail}")]
	private static partial void GameVersionNotWritten(ILogger logger, string detail);
}

/// <summary>Тело захвата.</summary>
public sealed record ClaimRequest
{
	[JsonPropertyName("token")] public string Token { get; init; } = string.Empty;

	[JsonPropertyName("username")] public string Username { get; init; } = "admin";

	[JsonPropertyName("password")] public string Password { get; init; } = string.Empty;
}

/// <summary>Тело шага «Доступ».</summary>
public sealed record AccessRequest
{
	[JsonPropertyName("bind")] public string Bind { get; init; } = "127.0.0.1";

	[JsonPropertyName("port")] public long Port { get; init; } = 8080;

	[JsonPropertyName("allowed_hosts")] public string AllowedHosts { get; init; } = "localhost,127.0.0.1";

	[JsonPropertyName("secure_cookie")] public bool SecureCookie { get; init; }
}

/// <summary>Тело завершения.</summary>
public sealed record FinishRequest
{
	[JsonPropertyName("restart")] public bool Restart { get; init; } = true;

	/// <summary>Закрыть мастер, не создавая сервера (2.0).</summary>
	[JsonPropertyName("without_server")] public bool WithoutServer { get; init; }
}
