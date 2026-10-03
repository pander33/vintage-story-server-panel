using System.Security.Cryptography;
using Panel.Access.Passwords;
using Panel.SharedKernel;

namespace Panel.Access.Domain;

/// <summary>Кто вошёл: то, из чего выписывается сессия.</summary>
public sealed record AuthenticatedUser(UserName Name, Role Role, int Generation);

/// <summary>
/// Все учётные записи панели как один агрегат.
/// </summary>
/// <remarks>
/// Правила относятся к набору записей («admin нельзя удалить», «admin остаётся
/// администратором»), поэтому агрегат — весь справочник.
///
/// Справочник неизменяемый: изменяющие операции возвращают новый, а на диск его пишет
/// хранилище.
/// </remarks>
public sealed class AccountDirectory
{
	private readonly Dictionary<string, Account> accounts;

	public AccountDirectory(IEnumerable<Account> accounts)
	{
		ArgumentNullException.ThrowIfNull(accounts);
		this.accounts = accounts.ToDictionary(account => account.Name.Value, StringComparer.Ordinal);
	}

	public IReadOnlyCollection<Account> All => [.. accounts.Values.OrderBy(a => a.Name.Value, StringComparer.Ordinal)];

	public bool IsEmpty => accounts.Count == 0;

	public Account? Find(UserName name) => accounts.GetValueOrDefault(name.Value);

	/// <summary>
	/// Вход. Неизвестное имя, неверный пароль и неверный код дают один и тот же отказ.
	/// </summary>
	public Result<AuthenticatedUser> Authenticate(UserName name, Credentials credentials, DateTimeOffset now)
	{
		var account = Find(name);
		return account is not null && account.Accepts(credentials, now)
			? new AuthenticatedUser(account.Name, account.Role, account.Generation)
			: Failure.Unauthenticated("Неверные учётные данные");
	}

	/// <summary>
	/// Действительна ли сессия: учётная запись существует и её поколение не изменилось.
	/// Подпись токена проверяется раньше. Роль берётся из учётной записи, а не из токена,
	/// чтобы понижение в правах действовало сразу.
	/// </summary>
	public Result<AuthenticatedUser> Validate(UserName name, int generation)
	{
		var account = Find(name);
		if (account is null || account.Generation != generation)
		{
			return Failure.Unauthenticated("Требуется вход");
		}

		return new AuthenticatedUser(account.Name, account.Role, account.Generation);
	}

	/// <summary>
	/// Создать или заменить учётную запись. Поколение увеличивается всегда: и смена пароля,
	/// и смена роли отзывают выданные сессии.
	/// </summary>
	/// <remarks>
	/// Новая запись начинает со случайного поколения, а не с единицы. Сессии и API-токены удалённой
	/// записи хранят её имя и поколение; запись с тем же именем, начавшая бы с прежнего числа,
	/// вернула бы их к жизни, а токены без срока — навсегда.
	/// </remarks>
	public Result<AccountDirectory> Put(UserName name, PasswordHash password, Role role)
	{
		if (name.IsAdministrator && role != Role.Admin)
		{
			return Failure.Invalid("Основная учётная запись admin должна оставаться администратором");
		}

		var current = Find(name);
		var updated = current is null
			? new Account { Name = name, Password = password, Role = role, Generation = FirstGeneration() }
			: current with { Password = password, Role = role, Generation = current.Generation + 1 };

		return With(updated);
	}

	public Result<AccountDirectory> Delete(UserName name)
	{
		if (name.IsAdministrator)
		{
			return Failure.Invalid("Основную учётную запись admin удалить нельзя");
		}

		if (Find(name) is null)
		{
			return Failure.NotFound($"Учётная запись {name} не найдена");
		}

		return new AccountDirectory(accounts.Values.Where(account => account.Name != name));
	}

	/// <summary>
	/// Начать настройку второго фактора: секрет запоминается как ожидающий и
	/// действующим не становится, пока не подтверждён кодом.
	/// </summary>
	public Result<(AccountDirectory Directory, string Secret)> BeginTotp(UserName name)
	{
		var account = Find(name);
		if (account is null)
		{
			return Failure.NotFound($"Учётная запись {name} не найдена");
		}

		var secret = Totp.NewSecret();
		var updated = With(account with { PendingTotpSecret = secret });
		return updated.IsSuccess
			? Result.Ok((updated.Value, secret))
			: Result.Fail<(AccountDirectory, string)>(updated.Failure!);
	}

	public Result<AccountDirectory> ConfirmTotp(UserName name, OneTimeCode code, DateTimeOffset now)
	{
		var account = Find(name);
		if (account is null)
		{
			return Failure.NotFound($"Учётная запись {name} не найдена");
		}

		if (!Totp.Verify(account.PendingTotpSecret, code.Value, now))
		{
			return Failure.Invalid("Неверный одноразовый код");
		}

		return With(account with
		{
			TotpSecret = account.PendingTotpSecret,
			PendingTotpSecret = string.Empty,
			TotpEnabled = true,
		});
	}

	public Result<AccountDirectory> DisableTotp(UserName name)
	{
		var account = Find(name);
		if (account is null)
		{
			return Failure.NotFound($"Учётная запись {name} не найдена");
		}

		return With(account with
		{
			TotpEnabled = false,
			TotpSecret = string.Empty,
			PendingTotpSecret = string.Empty,
		});
	}

	/// <summary>
	/// Случайное начало из [2^20, 2^30): далеко от поколений записей, созданных с единицы, и с
	/// запасом до переполнения при сменах пароля.
	/// </summary>
	private static int FirstGeneration() => RandomNumberGenerator.GetInt32(1 << 20, 1 << 30);

	private Result<AccountDirectory> With(Account account)
	{
		var accounts = new Dictionary<string, Account>(this.accounts, StringComparer.Ordinal)
		{
			[account.Name.Value] = account,
		};

		return new AccountDirectory(accounts.Values);
	}
}
