namespace Panel.Access.Domain;

/// <summary>
/// Три роли панели, как в accounts.py. Порядок объявления ничего не значит: права задаёт
/// таблица, а не сравнение значений.
/// </summary>
public enum Role
{
	Viewer,
	Operator,
	Admin,
}

public static class RoleWire
{
	public static string ToWire(this Role role) => role switch
	{
		Role.Operator => "operator",
		Role.Admin => "admin",
		_ => "viewer",
	};

	/// <summary>
	/// Неизвестная роль считается наблюдателем: ошибка разбора не должна повышать права.
	/// </summary>
	public static Role FromWire(string? wire) => wire switch
	{
		"admin" => Role.Admin,
		"operator" => Role.Operator,
		_ => Role.Viewer,
	};
}

/// <summary>
/// Старшинство ролей для токенов: наблюдатель ниже оператора, оператор ниже администратора.
/// Права по-прежнему задаёт таблица (<see cref="RolePermissions"/>); старшинство отвечает только
/// на вопрос «не больше ли это, чем у владельца».
/// </summary>
public static class RoleOrder
{
	public static bool AtMost(Role role, Role limit) => Rank(role) <= Rank(limit);

	public static Role Lower(Role left, Role right) => Rank(left) <= Rank(right) ? left : right;

	private static int Rank(Role role) => role switch
	{
		Role.Admin => 2,
		Role.Operator => 1,
		_ => 0,
	};
}
