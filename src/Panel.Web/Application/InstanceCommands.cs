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

/// <summary>Переименовать и «забыть» может администратор: <c>/api/instances</c> закрыт для оператора.</summary>
[Requires(Permission.Administer)]
public sealed record ManageInstances : ICommand;
