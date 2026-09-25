namespace Panel.Access.Domain;

/// <summary>
/// Кто выполняет запрос. Создаётся только из проверенной сессии: конструктор внутренний,
/// и собрать удостоверение с произвольной ролью в другом контексте нельзя.
/// </summary>
public sealed record Identity
{
	internal Identity(string user, Role role, int generation, string csrfToken, DateTimeOffset expiresAt)
	{
		User = user;
		Role = role;
		Generation = generation;
		CsrfToken = csrfToken;
		ExpiresAt = expiresAt;
	}

	public string User { get; }

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
	internal Identity WithRole(Role role) => new(User, role, Generation, CsrfToken, ExpiresAt);

	public bool Can(Permission permission) => RolePermissions.Allows(Role, permission);

	/// <summary>
	/// Наблюдатель не видит адресов игроков: ни в таблице, ни в телеметрии, ни в журналах,
	/// ни в выгрузке. Это право, а маскирует адреса сериализация.
	/// </summary>
	public bool HidesAddresses => !Can(Permission.SeePlayerAddresses);
}
