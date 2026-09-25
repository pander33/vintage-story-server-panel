using Panel.Access.Authorization;
using Panel.Access.Domain;

namespace Panel.Web.Application;

/// <summary>
/// Состояние обновлений, история и план обновления игры доступны любой роли.
/// </summary>
[Requires(Permission.ObserveServer)]
public sealed record ReadUpdates : ICommand;

/// <summary>Пакетное обновление модов, обновление игры и откат доступны администратору.</summary>
[Requires(Permission.Administer)]
[MutatesServer]
public sealed record ChangeUpdates : ICommand;
