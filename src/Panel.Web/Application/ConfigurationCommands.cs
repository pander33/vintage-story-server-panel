using Panel.Access.Authorization;
using Panel.Access.Domain;

namespace Panel.Web.Application;

/// <summary>
/// Команды над настройками сервера.
/// </summary>
/// <remarks>
/// Чтение доступно любой роли, изменение — администратору. По тому же праву решается, кто
/// видит пароль входа на сервер: тот, кто может его менять.
/// </remarks>
[Requires(Permission.ObserveServer)]
public sealed record ReadServerConfig : ICommand;

[Requires(Permission.Administer)]
[MutatesServer]
public sealed record ChangeServerConfig : ICommand;

[Requires(Permission.ObserveServer)]
public sealed record ReadWorldTemplate : ICommand;

[Requires(Permission.Administer)]
[MutatesServer]
public sealed record ChangeWorldTemplate : ICommand;

[Requires(Permission.ObserveServer)]
public sealed record ReadLiveWorldConfig : ICommand;

/// <summary>
/// Изменение работающего мира доступно администратору: это не консольное действие оператора,
/// а правка параметров мира, которую нельзя откатить копией.
/// </summary>
[Requires(Permission.Administer)]
[MutatesServer]
public sealed record ChangeLiveWorldConfig : ICommand;
