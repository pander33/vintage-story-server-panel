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

/// <summary>
/// Списки забаненных игроков и белого списка (интерфейс 2.1 §3) — то же право, что у онлайн
/// игроков; UID виден только при <see cref="Permission.SeePlayerAddresses"/>.
/// </summary>
[Requires(Permission.ObserveServer)]
public sealed record ReadPlayerLists : ICommand;

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
/// Настройки уведомлений доступны администратору: в них адреса вебхуков и токен бота. Не по
/// API-токену (<see cref="RequiresSessionAttribute"/>): подменённый вебхук уносил бы события панели наружу.
/// </summary>
[Requires(Permission.Administer)]
[RequiresSession]
public sealed record ConfigureNotifications : ICommand;

/// <summary>
/// Метрики всех серверов для Prometheus (<c>GET /metrics</c>): те же числа, что видит любая роль
/// на «Обзоре», без имён и адресов игроков.
/// </summary>
[Requires(Permission.ObserveServer)]
public sealed record ExportMetrics : ICommand;
