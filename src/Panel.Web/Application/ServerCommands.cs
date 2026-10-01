using Panel.Access.Authorization;
using Panel.Access.Domain;

namespace Panel.Web.Application;

/// <summary>
/// Команды управления сервером; доступны оператору.
/// </summary>
[Requires(Permission.ControlService)]
[MutatesServer]
public sealed record ControlService : ICommand;

[Requires(Permission.ObserveServer)]
public sealed record ReadLogs : ICommand;

[Requires(Permission.ControlService)]
[MutatesServer]
public sealed record SendServerCommand : ICommand;

/// <summary>Произвольная строка консоли (ADR-17), только для администратора.</summary>
[Requires(Permission.SendRawConsole)]
[MutatesServer]
public sealed record SendRawConsole : ICommand;

/// <summary>Каталог действий доступен всем: он только описывает действия.</summary>
[Requires(Permission.ObserveServer)]
public sealed record ReadCommandCatalogue : ICommand;

/// <summary>
/// Проверка игрового порта из интернета. Сервер она не меняет, но обращается к внешнему сервису
/// и показывает внешний адрес машины, поэтому доступна оператору, а не наблюдателю.
/// </summary>
[Requires(Permission.ControlService)]
public sealed record CheckReachability : ICommand;
