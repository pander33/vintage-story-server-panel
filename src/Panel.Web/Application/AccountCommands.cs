using Panel.Access.Authorization;
using Panel.Access.Domain;

namespace Panel.Web.Application;

/// <summary>
/// Команды управления учётными записями.
/// </summary>
/// <remarks>
/// Список и правка доступны администратору, а настройка своего второго фактора — любому
/// вошедшему.
/// </remarks>
[Requires(Permission.Administer)]
public sealed record ListAccounts : ICommand;

[Requires(Permission.Administer)]
public sealed record PutAccount(UserName Name, Role Role) : ICommand;

[Requires(Permission.Administer)]
public sealed record DeleteAccount(UserName Name) : ICommand;

/// <summary>Настройка второго фактора своей учётной записи: доступна и наблюдателю.</summary>
[Requires(Permission.ObserveServer)]
public sealed record ManageOwnTotp : ICommand;
