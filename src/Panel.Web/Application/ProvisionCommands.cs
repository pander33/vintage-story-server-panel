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
/// <see cref="MutatesServerAttribute"/>: нового сервера ещё нет. Не по API-токену
/// (<see cref="RequiresSessionAttribute"/>): создание сервера открывает порт и запускает программу.
/// </summary>
[Requires(Permission.Administer)]
[RequiresSession]
public sealed record ManageProvision : ICommand;

/// <summary>
/// Каталог, в котором создаются новые серверы, меняет администратор, как и создаёт серверы.
/// Без <see cref="MutatesServerAttribute"/>: созданные серверы остаются на месте. Не по API-токену:
/// каталог определяет, где демон от имени root создаёт и запускает серверы.
/// </summary>
[Requires(Permission.Administer)]
[RequiresSession]
public sealed record ChangeServersRoot : ICommand;

/// <summary>
/// Мастер установки после захвата работает в обычной сессии администратора с CSRF. Для самого
/// захвата команды нет: администратора ещё нет, и его заменяет одноразовый токен. Не по API-токену:
/// мастер меняет адрес, порт и доступ панели.
/// </summary>
[Requires(Permission.Administer)]
[RequiresSession]
public sealed record RunSetupWizard : ICommand;
