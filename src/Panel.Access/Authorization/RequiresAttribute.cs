using Panel.Access.Domain;

namespace Panel.Access.Authorization;

/// <summary>
/// Право, необходимое для выполнения команды. Ставится на тип команды, а не на маршрут (ADR-03).
/// </summary>
/// <remarks>
/// Поэтому команда от планировщика проходит ту же проверку, что и команда из HTTP. Команда
/// без атрибута не проходит архитектурный тест.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class RequiresAttribute : Attribute
{
	public RequiresAttribute(Permission permission) => Permission = permission;

	public Permission Permission { get; }
}

/// <summary>Метка команды для архитектурного теста.</summary>
public interface ICommand;
