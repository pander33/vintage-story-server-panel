namespace Panel.Access.Domain;

/// <summary>
/// Право на действие. Проверяется по типу команды, а не по адресу запроса.
/// </summary>
/// <remarks>
/// Проверка по префиксу адреса зависела бы от одного символа: <c>/api/backups</c> и
/// <c>/api/backups/</c>. Права добавляются по мере появления команд.
/// </remarks>
public enum Permission
{
	/// <summary>Видеть состояние сервера. Доступно всем ролям, включая наблюдателя.</summary>
	ObserveServer,

	/// <summary>Видеть сетевые адреса игроков. Наблюдателю не выдаётся никогда.</summary>
	SeePlayerAddresses,

	/// <summary>Управлять службой: запуск, остановка, перезапуск.</summary>
	ControlService,

	/// <summary>Действия, доступные только администратору.</summary>
	Administer,

	/// <summary>
	/// Произвольная строка в консоль игры, только администратору. Отдельно от
	/// <see cref="ControlService"/>: через консоль можно выдать права, остановить сервер и
	/// сделать всё, что добавили моды.
	/// </summary>
	SendRawConsole,
}

/// <summary>
/// Права каждой роли одной таблицей.
/// </summary>
public static class RolePermissions
{
	private static readonly Dictionary<Role, HashSet<Permission>> table = new()
	{
		[Role.Viewer] = [Permission.ObserveServer],
		[Role.Operator] = [Permission.ObserveServer, Permission.SeePlayerAddresses, Permission.ControlService],
		[Role.Admin] =
		[
			Permission.ObserveServer,
			Permission.SeePlayerAddresses,
			Permission.ControlService,
			Permission.Administer,
			Permission.SendRawConsole,
		],
	};

	public static bool Allows(Role role, Permission permission) =>
		table.TryGetValue(role, out var granted) && granted.Contains(permission);

	public static IReadOnlySet<Permission> Of(Role role) =>
		table.TryGetValue(role, out var granted) ? granted : new HashSet<Permission>();
}
