using Panel.Access.Authorization;
using Panel.Access.Domain;

namespace Panel.Web.Application;

/// <summary>Лента игровых событий (чат, входы, смерти) видна любой роли.</summary>
[Requires(Permission.ObserveServer)]
public sealed record ReadGameEvents : ICommand;

/// <summary>
/// Действие над игроком: белый список, бан, отключение, права, объявление. Доступно оператору:
/// это его обычная работа.
/// </summary>
[Requires(Permission.ControlService)]
public sealed record SendPlayerCommand : ICommand;

/// <summary>Игроки, телеметрия, производительность, метрики доступны любой роли.</summary>
[Requires(Permission.ObserveServer)]
public sealed record ReadTelemetry : ICommand;

/// <summary>Лента событий сервера доступна любой роли.</summary>
[Requires(Permission.ObserveServer)]
public sealed record ReadEvents : ICommand;

/// <summary>
/// Диагностический пакет доступен любой роли: адреса и секреты из него удалены для всех.
/// </summary>
[Requires(Permission.ObserveServer)]
public sealed record ReadDiagnostics : ICommand;

/// <summary>Журнал службы панели доступен любой роли; наблюдатель видит его без адресов.</summary>
[Requires(Permission.ObserveServer)]
public sealed record ReadPanelLogs : ICommand;

/// <summary>
/// Настройки уведомлений доступны администратору: в них адреса вебхуков и токен бота.
/// </summary>
[Requires(Permission.Administer)]
public sealed record ConfigureNotifications : ICommand;
