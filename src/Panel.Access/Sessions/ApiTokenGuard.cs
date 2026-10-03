using Panel.Access.Domain;
using Panel.Access.Ports;
using Panel.SharedKernel;

namespace Panel.Access.Sessions;

/// <summary>
/// Проверка API-токена из заголовка <c>Authorization: Bearer</c>: формат, секрет, срок,
/// учётная запись владельца и её поколение.
/// </summary>
/// <remarks>
/// Как и <see cref="SessionGuard"/>, учётная запись перечитывается на каждый запрос: удалённая
/// запись, новый пароль или понижение роли действуют на токен сразу.
/// </remarks>
public sealed class ApiTokenGuard
{
	private readonly IApiTokenStore tokens;
	private readonly IAccountStore accounts;
	private readonly TimeProvider time;

	public ApiTokenGuard(IApiTokenStore tokens, IAccountStore accounts, TimeProvider time)
	{
		ArgumentNullException.ThrowIfNull(tokens);
		ArgumentNullException.ThrowIfNull(accounts);
		ArgumentNullException.ThrowIfNull(time);
		this.tokens = tokens;
		this.accounts = accounts;
		this.time = time;
	}

	public async Task<Result<Identity>> AuthenticateAsync(string? presented, CancellationToken ct)
	{
		var parsed = PresentedApiToken.Parse(presented);
		if (!parsed.IsSuccess)
		{
			return parsed.Failure!;
		}

		var set = await tokens.LoadAsync(ct).ConfigureAwait(false);
		if (!set.IsSuccess)
		{
			return set.Failure!;
		}

		var now = time.GetUtcNow();
		var token = set.Value.Authenticate(parsed.Value, now);
		if (!token.IsSuccess)
		{
			return token.Failure!;
		}

		var directory = await accounts.LoadAsync(ct).ConfigureAwait(false);
		if (!directory.IsSuccess)
		{
			return directory.Failure!;
		}

		var owner = directory.Value.Validate(token.Value.User, token.Value.Generation);
		return owner.IsSuccess
			? Identity.ForToken(token.Value, owner.Value.Role)
			: Failure.Unauthenticated("Недействительный API-токен");
	}
}
