using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Panel.SharedKernel;

namespace Panel.Access.Domain;

/// <summary>Идентификатор API-токена: шестнадцать строчных шестнадцатеричных цифр.</summary>
public readonly record struct ApiTokenId
{
	public const int Length = 16;

	private ApiTokenId(string value) => Value = value;

	public string Value { get; }

	public static Result<ApiTokenId> Parse(string? raw) =>
		raw is { Length: Length } && raw.All(char.IsAsciiHexDigitLower)
			? new ApiTokenId(raw)
			: Failure.NotFound("API-токен не найден");

	internal static ApiTokenId New() => new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(Length / 2)));

	public override string ToString() => Value ?? string.Empty;
}

/// <summary>Имя API-токена, по которому владелец узнаёт его в списке и в журнале аудита.</summary>
public readonly record struct ApiTokenName
{
	public const int MaxLength = 64;

	private ApiTokenName(string value) => Value = value;

	public string Value { get; }

	public static Result<ApiTokenName> Create(string? raw)
	{
		var name = raw?.Trim() ?? string.Empty;
		if (name.Length == 0 || name.Length > MaxLength || name.Any(char.IsControl))
		{
			return Failure.Invalid($"Имя токена: от 1 до {MaxLength} символов без управляющих");
		}

		return new ApiTokenName(name);
	}

	public override string ToString() => Value ?? string.Empty;
}

/// <summary>
/// Токен в том виде, в каком его прислали: <c>vsp_&lt;id&gt;_&lt;секрет&gt;</c>. Секрет —
/// 32 случайных байта в base64url; панель хранит только его SHA-256.
/// </summary>
/// <remarks>
/// Префикс нужен людям и сканерам утечек: строку <c>vsp_…</c> в журнале или репозитории легко
/// найти. Идентификатор позволяет найти запись без перебора хешей.
/// </remarks>
public readonly record struct PresentedApiToken
{
	public const string Prefix = "vsp_";

	private const int secretBytes = 32;

	private PresentedApiToken(ApiTokenId id, string secret)
	{
		Id = id;
		Secret = secret;
	}

	public ApiTokenId Id { get; }

	internal string Secret { get; }

	public static Result<PresentedApiToken> Parse(string? raw)
	{
		// vsp_ + 16 цифр + _ + секрет: разбор по позициям, потому что в base64url есть и «_».
		var separator = Prefix.Length + ApiTokenId.Length;
		if (raw is null
			|| raw.Length <= separator + 1
			|| !raw.StartsWith(Prefix, StringComparison.Ordinal)
			|| raw[separator] != '_')
		{
			return Failure.Unauthenticated("Недействительный API-токен");
		}

		var id = ApiTokenId.Parse(raw[Prefix.Length..separator]);
		return id.IsSuccess
			? new PresentedApiToken(id.Value, raw[(separator + 1)..])
			: Failure.Unauthenticated("Недействительный API-токен");
	}

	/// <summary>Новый токен: текст для владельца и хеш секрета для хранения.</summary>
	internal static (string Plain, ApiTokenId Id, string Hash) New()
	{
		var id = ApiTokenId.New();
		var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(secretBytes));
		return ($"{Prefix}{id.Value}_{secret}", id, HashOf(secret));
	}

	internal static string HashOf(string secret) =>
		Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}

/// <summary>
/// API-токен учётной записи: доступ скриптов и Prometheus без браузера и cookie.
/// </summary>
/// <remarks>
/// <para>
/// Права токена — его роль, но не выше роли владельца на момент запроса: понижение учётной записи
/// понижает и её токены. Поколение запоминается при выпуске, и смена пароля или роли (новое
/// поколение) отзывает все токены владельца, как и его сессии (ADR-06).
/// </para>
/// <para>
/// Хранится только SHA-256 секрета: секрет — 256 случайных бит, медленный хеш пароля здесь ничего
/// не добавил бы, а проверку каждого запроса замедлил бы.
/// </para>
/// </remarks>
public sealed record ApiToken
{
	public required ApiTokenId Id { get; init; }

	public required ApiTokenName Name { get; init; }

	public required UserName User { get; init; }

	public required Role Role { get; init; }

	public required int Generation { get; init; }

	/// <summary>SHA-256 секрета строчными шестнадцатеричными цифрами.</summary>
	public required string Hash { get; init; }

	public required DateTimeOffset CreatedAt { get; init; }

	/// <summary>Срок действия; без срока токен действует до отзыва.</summary>
	public DateTimeOffset? ExpiresAt { get; init; }

	public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expires && expires <= now;

	/// <summary>Принадлежит ли запись живой учётной записи того же поколения.</summary>
	public bool IsLive(AccountDirectory accounts, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(accounts);
		return !IsExpired(now) && accounts.Validate(User, Generation).IsSuccess;
	}

	internal bool Matches(PresentedApiToken presented) =>
		presented.Id == Id
		&& CryptographicOperations.FixedTimeEquals(
			Encoding.ASCII.GetBytes(PresentedApiToken.HashOf(presented.Secret)),
			Encoding.ASCII.GetBytes(Hash));
}

/// <summary>Выпущенный токен: запись и текст, который показывается владельцу один раз.</summary>
public sealed record IssuedApiToken(ApiTokenSet Tokens, ApiToken Token, string Plain);

/// <summary>Что просят при выпуске токена.</summary>
public sealed record ApiTokenDraft(ApiTokenName Name, Role Role, TimeSpan? Lifetime);

/// <summary>
/// Все API-токены панели как один агрегат: правила (предел на учётную запись, уборка мёртвых
/// записей) относятся к набору. Неизменяемый, как и <see cref="AccountDirectory"/>.
/// </summary>
public sealed class ApiTokenSet
{
	/// <summary>Сколько токенов может быть у одной учётной записи.</summary>
	public const int MaxPerUser = 20;

	/// <summary>Самый долгий срок, который можно задать.</summary>
	public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(3650);

	private readonly List<ApiToken> tokens;

	public ApiTokenSet(IEnumerable<ApiToken> tokens)
	{
		ArgumentNullException.ThrowIfNull(tokens);
		this.tokens = [.. tokens];
	}

	public static ApiTokenSet Empty { get; } = new([]);

	public IReadOnlyList<ApiToken> All => tokens;

	/// <summary>Токен владельца: роль не выше его роли, не больше <see cref="MaxPerUser"/> на запись.</summary>
	public Result<IssuedApiToken> Issue(AuthenticatedUser owner, ApiTokenDraft draft, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(owner);
		ArgumentNullException.ThrowIfNull(draft);

		if (!RoleOrder.AtMost(draft.Role, owner.Role))
		{
			return Failure.Forbidden("Токен не может давать больше прав, чем у его владельца");
		}

		if (draft.Lifetime is { } lifetime && (lifetime <= TimeSpan.Zero || lifetime > MaxLifetime))
		{
			return Failure.Invalid("Срок действия токена — от одного дня до десяти лет или без срока");
		}

		// Снимок учётных записей мог устареть до захвата затвора хранилища токенов: по нему нельзя
		// удалять записи другого поколения, выпущенные уже после смены пароля. Убираются только
		// истёкшие записи, а предел считает токены текущего поколения владельца.
		var unexpired = tokens.Where(token => !token.IsExpired(now)).ToList();
		if (unexpired.Count(token => token.User == owner.Name && token.Generation == owner.Generation) >= MaxPerUser)
		{
			return Failure.Conflict($"У учётной записи уже {MaxPerUser} токенов: отзовите ненужные");
		}

		var (plain, id, hash) = PresentedApiToken.New();
		var token = new ApiToken
		{
			Id = id,
			Name = draft.Name,
			User = owner.Name,
			Role = draft.Role,
			Generation = owner.Generation,
			Hash = hash,
			CreatedAt = now,
			ExpiresAt = draft.Lifetime is { } span ? now + span : null,
		};

		return new IssuedApiToken(new ApiTokenSet([.. unexpired, token]), token, plain);
	}

	/// <summary>
	/// Отзыв. Свой токен отзывает любой, чужой — только администратор; чужой токен для
	/// остальных не существует, чтобы по отказу нельзя было узнать чужие идентификаторы.
	/// </summary>
	public Result<ApiTokenSet> Revoke(ApiTokenId id, UserName by, bool administrator)
	{
		var token = tokens.Find(item => item.Id == id);
		if (token is null || (!administrator && token.User != by))
		{
			return Failure.NotFound("API-токен не найден");
		}

		return new ApiTokenSet(tokens.Where(item => item.Id != id));
	}

	/// <summary>Запись по предъявленному токену, если секрет верен и срок не вышел.</summary>
	public Result<ApiToken> Authenticate(PresentedApiToken presented, DateTimeOffset now)
	{
		var token = tokens.Find(item => item.Id == presented.Id);
		return token is not null && token.Matches(presented) && !token.IsExpired(now)
			? token
			: Failure.Unauthenticated("Недействительный API-токен");
	}

	/// <summary>
	/// Без мёртвых записей: истёкших, удалённых учётных записей и прежних поколений. Их не
	/// воскресить, а место в пределе они занимали бы.
	/// </summary>
	public ApiTokenSet Prune(AccountDirectory accounts, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(accounts);
		return new ApiTokenSet(tokens.Where(token => token.IsLive(accounts, now)));
	}
}
