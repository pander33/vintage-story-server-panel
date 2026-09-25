using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Panel.Access.Authorization;
using Panel.Access.Domain;
using Panel.Access.Passwords;
using Panel.Access.Ports;
using Panel.Contracts.Http;
using Panel.SharedKernel;
using Panel.Web.Application;

namespace Panel.Web.Endpoints;

public static class AccountEndpoints
{
	public static void MapAccounts(this IEndpointRouteBuilder routes)
	{
		routes.MapGet("/api/accounts", async (
			HttpContext http, CommandAuthorizer authorizer, IAccountStore accounts, CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ListAccounts), requireCsrf: false);
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var directory = await accounts.LoadAsync(ct).ConfigureAwait(false);
			return directory.Match(
				value => PanelResults.Json(new AccountsResponse
				{
					// Хеш пароля и секрет второго фактора наружу не отдаются: список нужен только интерфейсу.
					Users = [.. value.All.Select(account => new AccountSummary
					{
						Username = account.Name.Value,
						Role = account.Role.ToWire(),
						TotpEnabled = account.TotpEnabled,
					})],
				}),
				PanelResults.Refuse);
		});

		routes.MapPut("/api/accounts", async (
			HttpContext http,
			AccountRequest body,
			CommandAuthorizer authorizer,
			IAccountStore accounts,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var name = UserName.Create(body.Username);
			if (!name.IsSuccess)
			{
				return PanelResults.Refuse(name.Failure!);
			}

			var role = ParseRole(body.Role);
			if (!role.IsSuccess)
			{
				return PanelResults.Refuse(role.Failure!);
			}

			var allowed = PanelResults.Authorize(
				http, authorizer, typeof(PutAccount));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var password = PasswordHash.Create(body.Password);
			if (!password.IsSuccess)
			{
				return PanelResults.Refuse(password.Failure!);
			}

			return await MutateAsync(
				accounts, audit, http, "account_saved", $"{name.Value}:{role.Value.ToWire()}", ct,
				directory => directory.Put(name.Value, password.Value, role.Value)).ConfigureAwait(false);
		});

		routes.MapDelete("/api/accounts/{username}", async (
			HttpContext http,
			string username,
			CommandAuthorizer authorizer,
			IAccountStore accounts,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var name = UserName.Create(username);
			if (!name.IsSuccess)
			{
				return PanelResults.Refuse(StatusCodes.Status404NotFound, "Пользователь не найден");
			}

			var allowed = PanelResults.Authorize(http, authorizer, typeof(DeleteAccount));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			return await MutateAsync(
				accounts, audit, http, "account_deleted", name.Value.Value, ct,
				directory => directory.Delete(name.Value)).ConfigureAwait(false);
		});

		MapTotp(routes);
	}

	private static void MapTotp(IEndpointRouteBuilder routes)
	{
		routes.MapPost("/api/accounts/totp/begin", async (
			HttpContext http,
			TotpRequest body,
			CommandAuthorizer authorizer,
			IAccountStore accounts,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ManageOwnTotp));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var directory = await accounts.LoadAsync(ct).ConfigureAwait(false);
			if (!directory.IsSuccess)
			{
				return PanelResults.Refuse(directory.Failure!);
			}

			var name = UserName.Parse(allowed.Value.User);
			var account = directory.Value.Find(name);

			// Текущий пароль запрашивается ещё раз: сессию могли оставить открытой, а включение второго
			// фактора меняет условия входа.
			if (account is null || !account.Password.Verify(body.Password))
			{
				return PanelResults.Refuse(StatusCodes.Status403Forbidden, "Подтвердите текущий пароль");
			}

			var begun = directory.Value.BeginTotp(name);
			if (!begun.IsSuccess)
			{
				return PanelResults.Refuse(begun.Failure!);
			}

			var saved = await accounts.SaveAsync(begun.Value.Directory, ct).ConfigureAwait(false);
			if (!saved.IsSuccess)
			{
				return PanelResults.Refuse(saved.Failure!);
			}

			await audit.RecordAsync(http, "totp_begin", name.Value, ct)
				.ConfigureAwait(false);

			var label = Uri.EscapeDataString("Vintage Story Panel:" + name.Value);
			return PanelResults.Json(new TotpBeginResponse
			{
				Secret = begun.Value.Secret,
				Uri = $"otpauth://totp/{label}?secret={begun.Value.Secret}&issuer=Vintage%20Story%20Panel",
			});
		});

		routes.MapPost("/api/accounts/totp/confirm", async (
			HttpContext http,
			TotpRequest body,
			CommandAuthorizer authorizer,
			IAccountStore accounts,
			AuditLog audit,
			TimeProvider time,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ManageOwnTotp));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var code = OneTimeCode.Create(body.Code);
			if (!code.IsSuccess)
			{
				return PanelResults.Refuse(StatusCodes.Status400BadRequest, "Неверный одноразовый код");
			}

			var name = UserName.Parse(allowed.Value.User);
			return await MutateAsync(
				accounts, audit, http, "totp_enabled", name.Value, ct,
				directory => directory.ConfirmTotp(name, code.Value, time.GetUtcNow())).ConfigureAwait(false);
		});

		// DELETE с телом: фронтенд отправляет {"password": …} методом DELETE. Тело указано явно,
		// потому что для DELETE минимальный API его не выводит.
		routes.MapDelete("/api/accounts/totp", async (
			HttpContext http,
			[FromBody] TotpRequest body,
			CommandAuthorizer authorizer,
			IAccountStore accounts,
			AuditLog audit,
			CancellationToken ct) =>
		{
			var allowed = PanelResults.Authorize(http, authorizer, typeof(ManageOwnTotp));
			if (!allowed.IsSuccess)
			{
				return PanelResults.Refuse(allowed.Failure!);
			}

			var name = UserName.Parse(allowed.Value.User);

			// Отключение второго фактора ослабляет вход, и открытая сессия не должна позволять сделать
			// это одним щелчком, поэтому нужен пароль.
			var directory = await accounts.LoadAsync(ct).ConfigureAwait(false);
			if (!directory.IsSuccess)
			{
				return PanelResults.Refuse(directory.Failure!);
			}

			var account = directory.Value.Find(name);
			if (account is null || !account.Password.Verify(body.Password))
			{
				return PanelResults.Refuse(StatusCodes.Status403Forbidden, "Подтвердите текущий пароль");
			}

			return await MutateAsync(
				accounts, audit, http, "totp_disabled", name.Value, ct,
				current => current.DisableTotp(name)).ConfigureAwait(false);
		});
	}

	/// <summary>
	/// Прочитать, применить правило, записать. Справочник загружается непосредственно перед
	/// изменением: его мог изменить другой запрос.
	/// </summary>
	private static async Task<IResult> MutateAsync(
		IAccountStore accounts,
		AuditLog audit,
		HttpContext http,
		string action,
		string detail,
		CancellationToken ct,
		Func<AccountDirectory, Result<AccountDirectory>> change)
	{
		var directory = await accounts.LoadAsync(ct).ConfigureAwait(false);
		if (!directory.IsSuccess)
		{
			return PanelResults.Refuse(directory.Failure!);
		}

		var changed = change(directory.Value);
		if (!changed.IsSuccess)
		{
			return PanelResults.Refuse(changed.Failure!);
		}

		var saved = await accounts.SaveAsync(changed.Value, ct).ConfigureAwait(false);
		if (!saved.IsSuccess)
		{
			return PanelResults.Refuse(saved.Failure!);
		}

		await audit.RecordAsync(http, action, detail, ct).ConfigureAwait(false);
		return PanelResults.Json(OkResponse.Instance);
	}

	/// <summary>
	/// Неизвестная роль — отказ, а не понижение до наблюдателя. При чтении токена умолчание
	/// защищает, а здесь оно создало бы учётную запись не с той ролью, которую просили.
	/// </summary>
	private static Result<Role> ParseRole(string raw) => raw switch
	{
		"viewer" => Role.Viewer,
		"operator" => Role.Operator,
		"admin" => Role.Admin,
		_ => Failure.Invalid("Недопустимая роль"),
	};
}
