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
[RequiresSession]
public sealed record ListAccounts : ICommand;

[Requires(Permission.Administer)]
[RequiresSession]
public sealed record PutAccount(UserName Name, Role Role) : ICommand;

[Requires(Permission.Administer)]
[RequiresSession]
public sealed record DeleteAccount(UserName Name) : ICommand;

/// <summary>Настройка второго фактора своей учётной записи: доступна и наблюдателю.</summary>
[Requires(Permission.ObserveServer)]
[RequiresSession]
public sealed record ManageOwnTotp : ICommand;

/// <summary>
/// Список API-токенов: свои — любому вошедшему, все — администратору. Как и выпуск и отзыв,
/// только из сессии браузера: токен не управляет токенами.
/// </summary>
[Requires(Permission.ObserveServer)]
[RequiresSession]
public sealed record ListApiTokens : ICommand;

/// <summary>Выпуск токена своей учётной записи; роль токена не выше своей.</summary>
[Requires(Permission.ObserveServer)]
[RequiresSession]
public sealed record IssueApiToken : ICommand;

/// <summary>Отзыв своего токена; чужой отзывает только администратор (решает агрегат).</summary>
[Requires(Permission.ObserveServer)]
[RequiresSession]
public sealed record RevokeApiToken : ICommand;
