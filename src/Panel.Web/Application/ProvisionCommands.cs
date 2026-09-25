using Panel.Access.Authorization;
using Panel.Access.Domain;

namespace Panel.Web.Application;

/// <summary>
/// Состояние создания серверов доступно администратору.
/// </summary>
[Requires(Permission.Administer)]
public sealed record ReadProvision : ICommand;

/// <summary>
/// Проверка машины и создание сервера доступны администратору. Без
/// <see cref="MutatesServerAttribute"/>: нового сервера ещё нет.
/// </summary>
[Requires(Permission.Administer)]
public sealed record ManageProvision : ICommand;

/// <summary>
/// Каталог, в котором создаются новые серверы, меняет администратор, как и создаёт серверы.
/// Без <see cref="MutatesServerAttribute"/>: созданные серверы остаются на месте.
/// </summary>
[Requires(Permission.Administer)]
public sealed record ChangeServersRoot : ICommand;

/// <summary>
/// Мастер установки после захвата работает в обычной сессии администратора с CSRF. Для самого
/// захвата команды нет: администратора ещё нет, и его заменяет одноразовый токен.
/// </summary>
[Requires(Permission.Administer)]
public sealed record RunSetupWizard : ICommand;
