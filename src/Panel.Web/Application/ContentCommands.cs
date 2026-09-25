using Panel.Access.Authorization;
using Panel.Access.Domain;

namespace Panel.Web.Application;

/// <summary>
/// Команды над содержимым сервера.
/// </summary>
/// <remarks>
/// Моды, миры и настройки модов — администратору, снятие копии — оператору, восстановление и
/// очистка копий — администратору.
/// </remarks>
[Requires(Permission.ObserveServer)]
public sealed record ReadMods : ICommand;

[Requires(Permission.Administer)]
[MutatesServer]
public sealed record ChangeMods : ICommand;

[Requires(Permission.ObserveServer)]
public sealed record ReadWorlds : ICommand;

[Requires(Permission.Administer)]
[MutatesServer]
public sealed record ChangeWorlds : ICommand;

[Requires(Permission.ObserveServer)]
public sealed record ReadBackups : ICommand;

/// <summary>
/// Снятие копии доступно оператору: он делает его перед любым действием, и требование прав
/// администратора подталкивало бы действовать без копии.
/// </summary>
[Requires(Permission.ControlService)]
[MutatesServer]
public sealed record CreateBackup : ICommand;

/// <summary>
/// Восстановление, удаление и очистка копий доступны администратору: только эти действия
/// панели могут безвозвратно уничтожить данные.
/// </summary>
[Requires(Permission.Administer)]
[MutatesServer]
public sealed record ChangeBackups : ICommand;

[Requires(Permission.Administer)]
public sealed record ReadModConfigs : ICommand;

[Requires(Permission.Administer)]
[MutatesServer]
public sealed record ChangeModConfigs : ICommand;

/// <summary>История «на всякий случай» видна любой роли.</summary>
[Requires(Permission.ObserveServer)]
public sealed record ReadHistory : ICommand;

/// <summary>
/// Удаление истории доступно администратору: это слой восстановления.
/// </summary>
[Requires(Permission.Administer)]
public sealed record ChangeHistory : ICommand;
