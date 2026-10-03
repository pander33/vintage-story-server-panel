namespace Panel.Access.Domain;

/// <summary>
/// Кто выполняет запрос. Создаётся только из проверенной сессии: конструктор внутренний,
/// и собрать удостоверение с произвольной ролью в другом контексте нельзя.
/// </summary>
public sealed record Identity
{
	internal Identity(string user, Role role, int generation, string csrfToken, DateTimeOffset expiresAt, string? apiToken = null)
	{
		User = user;
		Role = role;
		Generation = generation;
		CsrfToken = csrfToken;
		ExpiresAt = expiresAt;
		ApiToken = apiToken;
	}

	/// <summary>
	/// Удостоверение по API-токену: роль — меньшая из роли токена и текущей роли владельца.
	/// CSRF-токена нет: запрос с заголовком <c>Authorization</c> не может прислать чужой сайт.
	/// </summary>
	internal static Identity ForToken(ApiToken token, Role ownerRole) => new(
		token.User.Value,
		RoleOrder.Lower(token.Role, ownerRole),
		token.Generation,
		string.Empty,
		token.ExpiresAt ?? DateTimeOffset.MaxValue,
		token.Name.Value);

	public string User { get; }

	/// <summary>Имя API-токена, если запрос пришёл с ним, а не с сессией браузера.</summary>
	public string? ApiToken { get; }

	public bool ViaApiToken => ApiToken is not null;

	public Role Role { get; }

	/// <summary>
	/// Поколение учётной записи: при его смене все выданные сессии перестают действовать (ADR-06).
	/// </summary>
	public int Generation { get; }

	public string CsrfToken { get; }

	public DateTimeOffset ExpiresAt { get; }

	/// <summary>
	/// Удостоверение с ролью, перечитанной из учётной записи. Метод внутренний, чтобы роль
	/// нельзя было подменить за пределами Access.
	/// </summary>
	internal Identity WithRole(Role role) => new(User, role, Generation, CsrfToken, ExpiresAt, ApiToken);

	public bool Can(Permission permission) => RolePermissions.Allows(Role, permission);

	/// <summary>
	/// Наблюдатель не видит адресов игроков: ни в таблице, ни в телеметрии, ни в журналах,
	/// ни в выгрузке. Это право, а маскирует адреса сериализация.
	/// </summary>
	public bool HidesAddresses => !Can(Permission.SeePlayerAddresses);
}
