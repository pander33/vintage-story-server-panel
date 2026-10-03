using Panel.Access.Authorization;
using Panel.Access.Domain;

namespace Panel.Web.Application;

/// <summary>Перечень серверов доступен любой роли: каждому нужно видеть, какой сервер выбран.</summary>
[Requires(Permission.ObserveServer)]
public sealed record ReadInstances : ICommand;

/// <summary>
/// Выбор сервера доступен любой роли, но с CSRF: запрос меняет cookie, хотя и не сервер.
/// </summary>
[Requires(Permission.ObserveServer)]
public sealed record SelectInstance : ICommand;

/// <summary>
/// Переименовать, «забыть» и удалить сервер может администратор: <c>/api/instances</c> закрыт для
/// оператора. Не по API-токену (<see cref="RequiresSessionAttribute"/>): удаление необратимо.
/// </summary>
[Requires(Permission.Administer)]
[RequiresSession]
public sealed record ManageInstances : ICommand;
