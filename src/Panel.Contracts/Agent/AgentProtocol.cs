namespace Panel.Contracts.Agent;

/// <summary>
/// Протокол привилегированной стороны (ADR-02): один запрос — один JSON в ответ, по
/// долгоживущему соединению.
/// </summary>
/// <remarks>
/// Это транспорт, а не интерфейс <c>IAgent</c> с 62 методами (ADR-01). Прикладные порты
/// (<c>IServerRuntime</c> и другие) узкие и находятся в своих контекстах.
/// </remarks>
public static class AgentProtocol
{
	/// <summary>Версия протокола. При несовпадении запрос отвергается.</summary>
	public const int Version = 1;

	/// <summary>Предел одного кадра. Ответ больше этого — ошибка, а не большой мир.</summary>
	public const int MaxFrameBytes = 4 * 1024 * 1024;

	public const string OpStatus = "status";
	public const string OpTelemetry = "telemetry";
	public const string OpPing = "ping";
	public const string OpService = "service";
	public const string OpLogs = "logs";
	public const string OpLogsFrom = "logs-from";
	public const string OpConsole = "console";

	/// <summary>Произвольная строка консоли (ADR-17). Демон заново проверяет её через <c>RawConsoleLine</c>.</summary>
	public const string OpConsoleRaw = "console-raw";
	public const string OpCapabilities = "capabilities";

	// Содержимое сервера.
	public const string OpMods = "mods";
	public const string OpModInstall = "mod-install";
	public const string OpModToggle = "mod-toggle";
	public const string OpModDelete = "mod-delete";
	public const string OpModRollback = "mod-rollback";
	public const string OpModProfileSave = "mod-profile-save";
	public const string OpModProfileApply = "mod-profile-apply";
	public const string OpModProfileDelete = "mod-profile-delete";
	public const string OpModConfigs = "mod-configs";
	public const string OpModConfigGet = "mod-config-get";
	public const string OpModConfigPut = "mod-config-put";

	public const string OpWorlds = "worlds";
	public const string OpWorldImport = "world-import";
	public const string OpWorldExport = "world-export";
	public const string OpWorldClone = "world-clone";
	public const string OpWorldActivate = "world-activate";
	public const string OpWorldCreate = "world-create";
	public const string OpWorldPutAway = "world-archive";
	public const string OpWorldStored = "world-archives";
	public const string OpWorldRestore = "world-restore";
	public const string OpWorldStoredDelete = "world-archive-delete";

	public const string OpBackups = "backups";
	public const string OpBackupCreate = "backup-create";
	public const string OpBackupInspect = "backup-inspect";
	public const string OpBackupRestore = "backup-restore";
	public const string OpBackupDelete = "backup-delete";
	public const string OpBackupRestoreWorld = "backup-restore-world";
	public const string OpBackupRetention = "backup-retention";
	public const string OpTransferExport = "transfer-export";

	/// <summary>Ход идущей долгой операции сервера: стадия, прочитанные байты, прошедшее время.</summary>
	public const string OpOperation = "operation";

	// История «на всякий случай»: три команды над тремя хранилищами.
	public const string OpHistory = "history";
	public const string OpHistoryDelete = "history-delete";
	public const string OpHistoryPrune = "history-prune";

	// Настройки сервера. Живая конфигурация мира меняется отдельной операцией консоли, а не
	// записью serverconfig.json (INV-20).
	public const string OpConfigGet = "config-get";
	public const string OpConfigPut = "config-put";
	public const string OpConfigBackups = "config-backups";
	public const string OpConfigRollback = "config-rollback";
	public const string OpWorldConfigAction = "worldconfig-action";

	// Игроки и лента событий игры.
	public const string OpPlayerAction = "player-action";
	public const string OpGameEvents = "game-events";

	// Замер для истории метрик: служба, память, диск, мир, копии и телеметрия разом.
	public const string OpMetrics = "metrics";

	// Программа сервера: пакет игры, установка со снимком, откат, версия в реестре.
	public const string OpGamePackageInspect = "game-package-inspect";
	public const string OpGameHistory = "game-history";
	public const string OpGameUpdateApply = "game-update-apply";
	public const string OpGameUpdateRollback = "game-update-rollback";
	public const string OpPanelGameVersion = "panel-game-version";

	// Реестр: единственные операции, адресованные серверу "-", и только ему.
	public const string OpInstances = "instances";
	public const string OpInstanceRename = "instance-rename";
	public const string OpInstanceForget = "instance-forget";

	/// <summary>
	/// Удалить сервер целиком: юнит и всё дерево. Только для сервера, пути которого вывел демон;
	/// находится рядом с созданием, потому что использует ту же раскладку.
	/// </summary>
	public const string OpInstanceDelete = "instance-delete";

	// Создание сервера и операции самой панели тоже адресованы серверу "-": нужного сервера
	// ещё нет.
	public const string OpRuntimeProbe = "runtime-probe";
	public const string OpDiskFree = "disk-free";
	public const string OpInstanceCreate = "instance-create";
	public const string OpTransferInspect = "transfer-inspect";
	public const string OpInstanceImport = "instance-import";
	public const string OpInstanceServiceInstall = "instance-service-install";
	public const string OpPanelEnvSet = "panel-env-set";
	public const string OpPanelRestart = "panel-restart";

	/// <summary>
	/// Сменить каталог, в котором создаются новые серверы (<c>instances_root</c>). Созданные
	/// серверы не переезжают.
	/// </summary>
	public const string OpInstancesRootSet = "instances-root-set";

	/// <summary>Журнал юнита самой панели, общий для всех серверов.</summary>
	public const string OpPanelLogs = "panel-logs";
}
