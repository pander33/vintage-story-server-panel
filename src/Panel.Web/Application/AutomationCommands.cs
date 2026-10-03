using Panel.Access.Authorization;
using Panel.Access.Domain;

namespace Panel.Web.Application;

/// <summary>Расписание, прогоны и вердикт наблюдателя доступны любой роли.</summary>
[Requires(Permission.ObserveServer)]
public sealed record ReadAutomation : ICommand;

/// <summary>
/// Задания, запуск вручную, настройки наблюдателя и его внеочередная проверка доступны
/// администратору.
/// </summary>
[Requires(Permission.Administer)]
public sealed record ChangeAutomation : ICommand;

/// <summary>
/// Команда меняет сам сервер (службу, настройки, моды, миры, копии) и поэтому отклоняется с
/// 409, пока на сервере идёт обслуживание.
/// </summary>
/// <remarks>
/// Метка стоит на команде, как и право: запрет зависит от того, что делает
/// маршрут, а не от его адреса.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MutatesServerAttribute : Attribute;
