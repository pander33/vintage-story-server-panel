using Panel.Access.Domain;
using Panel.Access.Ports;
using Panel.SharedKernel;

namespace Panel.Access.Sessions;

/// <summary>
/// Полная проверка сессии: подпись, срок, существование учётной записи, поколение.
/// </summary>
/// <remarks>
/// Подпись подтверждает только то, что токен выдала панель; запись могли удалить, роль
/// понизить, пароль сменить. Поэтому обе проверки выполняются вместе и по отдельности не
/// вызываются.
/// </remarks>
public sealed class SessionGuard
{
	private readonly SessionReader reader;
	private readonly IAccountStore accounts;

	public SessionGuard(SessionReader reader, IAccountStore accounts)
	{
		ArgumentNullException.ThrowIfNull(reader);
		ArgumentNullException.ThrowIfNull(accounts);
		this.reader = reader;
		this.accounts = accounts;
	}

	public async Task<Result<Identity>> AuthenticateAsync(string? token, CancellationToken ct)
	{
		var claimed = reader.Read(token);
		if (!claimed.IsSuccess)
		{
			return claimed;
		}

		var name = UserName.Create(claimed.Value.User);
		if (!name.IsSuccess)
		{
			return Failure.Unauthenticated("Требуется вход");
		}

		var directory = await accounts.LoadAsync(ct).ConfigureAwait(false);
		if (!directory.IsSuccess)
		{
			// Учётные записи недоступны: пользователь должен увидеть «попробуйте позже»,
			// а не просьбу войти заново.
			return Result.Fail<Identity>(directory.Failure!);
		}

		var validated = directory.Value.Validate(name.Value, claimed.Value.Generation);
		if (!validated.IsSuccess)
		{
			return Failure.Unauthenticated("Требуется вход");
		}

		// Роль берётся из учётной записи, а не из токена: понижение в правах
		// действует немедленно, не дожидаясь истечения сессии.
		return claimed.Value.WithRole(validated.Value.Role);
	}
}
