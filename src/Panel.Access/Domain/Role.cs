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
