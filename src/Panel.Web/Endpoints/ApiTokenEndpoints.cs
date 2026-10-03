using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Panel.Access.Authorization;
using Panel.Access.Domain;
using Panel.Access.Ports;
using Panel.Contracts.Http;
using Panel.SharedKernel;
using Panel.Web.Application;

namespace Panel.Web.Endpoints;

/// <summary>
/// API-токены: выпуск, список и отзыв (findings §82). Всё — только из сессии браузера
/// (<see cref="RequiresSessionAttribute"/>): токен не управляет токенами.
/// </summary>
public static class ApiTokenEndpoints
{
	public static void MapApiTokens(this IEndpointRouteBuilder routes)
	{
		routes.MapGet("/api/tokens", async (
			HttpContext http,
			CommandAuthorizer authorizer,
			IApiTokenStore tokens,
			IAccountStore accounts,
			TimeProvider time,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ListApiTokens), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var directory = await accounts.LoadAsync(ct).ConfigureAwait(false);
			if (!directory.IsSuccess)
			{
				return PanelResults.Refuse(directory.Failure!);
			}

			var set = await tokens.LoadAsync(ct).ConfigureAwait(false);
			if (!set.IsSuccess)
			{
				return PanelResults.Refuse(set.Failure!);
			}

			var identity = allowed.Value;
			var me = UserName.Parse(identity.User);
			var everyone = identity.Can(Permission.Administer);
			var live = set.Value.Prune(directory.Value, time.GetUtcNow());

			return PanelResults.Json(new ApiTokensResponse
			{
				Tokens = [.. live.All
					.Where(token => everyone || token.User == me)
					.OrderBy(token => token.User.Value, StringComparer.Ordinal)
					.ThenBy(token => token.CreatedAt)
					.Select(Summary)],
				TotpEnabled = directory.Value.Find(me)?.TotpEnabled ?? false,
			});
		});

		routes.MapPost("/api/tokens", async (
			HttpContext http,
			ApiTokenRequest body,
			CommandAuthorizer authorizer,
			IApiTokenStore tokens,
			IAccountStore accounts,
			LoginThrottle throttle,
			AuditLog audit,
			TimeProvider time,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(IssueApiToken));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var name = ApiTokenName.Create(body.Name);
			if (!name.IsSuccess)
			{
				return PanelResults.Refuse(name.Failure!);
			}

			var role = AccountEndpoints.ParseRole(body.Role);
			if (!role.IsSuccess)
			{
				return PanelResults.Refuse(role.Failure!);
			}

			if (body.ExpiresDays < 0 || body.ExpiresDays > ApiTokenSet.MaxLifetime.TotalDays)
			{
				return PanelResults.Refuse(StatusCodes.Status400BadRequest, "Срок действия токена — от одного дня до десяти лет или без срока");
			}

			// Подтверждение паролем и кодом — та же попытка входа для ограничителя: иначе открытая
			// сессия позволяла бы подбирать пароль без предела.
			var address = PanelResults.ClientAddress(http);
			if (throttle.IsBlocked(address))
			{
				return PanelResults.Refuse(StatusCodes.Status429TooManyRequests, "Слишком много попыток. Подождите пять минут");
			}

			var directory = await accounts.LoadAsync(ct).ConfigureAwait(false);
			if (!directory.IsSuccess)
			{
				return PanelResults.Refuse(directory.Failure!);
			}

			var now = time.GetUtcNow();
			var me = UserName.Parse(allowed.Value.User);
			var code = OneTimeCode.Create(body.Code);
			var owner = directory.Value.Authenticate(
				me, new Credentials(body.Password, code.IsSuccess ? code.Value : OneTimeCode.None), now);
			if (!owner.IsSuccess)
			{
				// Неверный пароль из открытой сессии — такая же попытка подбора, как неудачный вход, и
				// след в аудите нужен тот же.
				throttle.RecordFailure(address);
				await audit.RecordAsync(http, "api_token_confirmation_failed", name.Value.Value, ct).ConfigureAwait(false);
				return PanelResults.Refuse(StatusCodes.Status403Forbidden, "Подтвердите текущий пароль и одноразовый код, если он включён");
			}

			throttle.Forget(address);
			var draft = new ApiTokenDraft(name.Value, role.Value, body.ExpiresDays == 0 ? null : TimeSpan.FromDays(body.ExpiresDays));
			var issued = await tokens.ChangeAsync(
				set => set.Issue(owner.Value, draft, now).Map(result => (result.Tokens, result)),
				ct).ConfigureAwait(false);
			if (!issued.IsSuccess)
			{
				return PanelResults.Refuse(issued.Failure!);
			}

			await audit.RecordAsync(http, "api_token_issued", $"{issued.Value.Token.Name.Value}:{role.Value.ToWire()}", ct)
				.ConfigureAwait(false);
			return PanelResults.Json(new ApiTokenIssuedResponse
			{
				Token = issued.Value.Plain,
				Summary = Summary(issued.Value.Token),
			});
		});

		routes.MapDelete("/api/tokens/{id}", async (
			HttpContext http,
			string id,
			CommandAuthorizer authorizer,
			IApiTokenStore tokens,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(RevokeApiToken));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var parsed = ApiTokenId.Parse(id);
			if (!parsed.IsSuccess)
			{
				return PanelResults.Refuse(parsed.Failure!);
			}

			var identity = allowed.Value;
			var revoked = await tokens.ChangeAsync(
				set =>
				{
					var token = set.All.FirstOrDefault(item => item.Id == parsed.Value);
					return set.Revoke(parsed.Value, UserName.Parse(identity.User), identity.Can(Permission.Administer))
						.Map(rest => (rest, token!));
				},
				ct).ConfigureAwait(false);
			if (!revoked.IsSuccess)
			{
				return PanelResults.Refuse(revoked.Failure!);
			}

			await audit.RecordAsync(http, "api_token_revoked", $"{revoked.Value.Name.Value}:{revoked.Value.User.Value}", ct)
				.ConfigureAwait(false);
			return PanelResults.Json(OkResponse.Instance);
		});
	}

	private static ApiTokenSummary Summary(ApiToken token) => new()
	{
		Id = token.Id.Value,
		Name = token.Name.Value,
		User = token.User.Value,
		Role = token.Role.ToWire(),
		CreatedAt = token.CreatedAt.ToUnixTimeSeconds(),
		ExpiresAt = token.ExpiresAt?.ToUnixTimeSeconds(),
	};
}
