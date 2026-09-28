using System.Collections.Concurrent;
using System.Reflection;
using Panel.Access.Domain;
using Panel.SharedKernel;

namespace Panel.Access.Authorization;

/// <summary>
/// Проверяет право по атрибуту на типе команды до вызова обработчика.
/// </summary>
public sealed class CommandAuthorizer
{
	private static readonly ConcurrentDictionary<Type, Permission> cache = new();

	public Result<Identity> Authorize(Identity identity, Type commandType)
	{
		ArgumentNullException.ThrowIfNull(identity);
		ArgumentNullException.ThrowIfNull(commandType);

		var permission = RequiredBy(commandType);
		return identity.Can(permission)
			? Result.Ok(identity)
			: Failure.Forbidden(Refusal(identity.Role, permission));
	}

	public Result<Identity> Authorize<TCommand>(Identity identity)
		where TCommand : ICommand => Authorize(identity, typeof(TCommand));

	/// <summary>
	/// Право, объявленное на типе команды. Без атрибута команда не выполняется: разрешения по умолчанию нет.
	/// </summary>
	public static Permission RequiredBy(Type commandType) =>
		cache.GetOrAdd(commandType, static type =>
			type.GetCustomAttribute<RequiresAttribute>()?.Permission
			?? throw new InvalidOperationException(
				$"У команды {type.FullName} нет атрибута [Requires]. Право обязано быть объявлено."));

	/// <summary>
	/// Тексты отказов: с ними посимвольно сравниваются золотые фикстуры.
	/// </summary>
	private static Phrase Refusal(Role role, Permission permission) => (role, permission) switch
	{
		(Role.Viewer, _) => Phrase.From("Роль наблюдателя не разрешает изменения"),
		(Role.Operator, Permission.Administer) => Phrase.From("Для этого действия требуется роль администратора"),
		_ => Phrase.From("Недостаточно прав для этого действия"),
	};
}
