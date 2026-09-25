using System.Text.Json.Nodes;
using Panel.SharedKernel;

namespace Panel.Maintenance.Domain;

/// <summary>Идентификатор операции: 32 шестнадцатеричных знака, как <c>uuid4().hex</c>.</summary>
public readonly record struct OperationId(Guid Value)
{
	public static OperationId New() => new(Guid.NewGuid());

	public static OperationId? Parse(string? text) =>
		Guid.TryParseExact(text, "N", out var value) ? new OperationId(value) : null;

	public override string ToString() => Value.ToString("N");
}

/// <summary>Вид операции: поле <c>type</c> записи <c>updates.json</c>.</summary>
public enum OperationKind
{
	Mods,
	Game,
	GameRollback,

	/// <summary>Операция не началась: отказ до появления её записи.</summary>
	Preflight,

	/// <summary>Создание сервера: запись <c>provision.json</c>, тип <c>instance</c>.</summary>
	Provision,

	/// <summary>Создание не началось: тип <c>preflight</c>, но с отдельным заголовком.</summary>
	ProvisionPreflight,
}

/// <summary>
/// Семейство операций: у обновлений и создания серверов свои файлы, текст прерывания и
/// префикс событий, а форма записи общая (INV-29).
/// </summary>
public enum OperationFamily
{
	Updates,
	Provision,
}

public enum OperationStatus
{
	Running,
	Success,
	Failed,
	Interrupted,
}

/// <summary>
/// Текущий этап операции, поле <c>phase</c>: имя вызываемой операции демона.
/// </summary>
public enum OperationPhase
{
	Mods,
	Status,
	PlayerAction,
	Service,
	BackupCreateOffline,
	BackupInspect,
	BackupRestore,
	ModInstall,
	GamePackageInspect,
	GameUpdateApply,
	GameUpdateRollback,
	PanelGameVersion,

	// Создание сервера: phase — имя последнего шага, а не операции демона.
	Catalogue,
	Download,
	Create,
	Start,
	Ready,
	Rollback,
}

/// <summary>Шаг операции: поле <c>action</c> элемента <c>steps</c>.</summary>
public enum StepAction
{
	Validate,
	Download,
	Backup,
	Install,
	Version,
	Rollback,
	RollbackData,
	RestartAfterFailedRollback,
	Catalogue,
	Create,
	Service,
	Start,
	Ready,
}

public enum StepResult
{
	Ok,
	Failed,

	/// <summary>Сервер создан, но готовность не подтвердилась.</summary>
	Warning,

	/// <summary>Шаг не выполнялся: зарегистрированный сервер не удаляется.</summary>
	Skipped,
}

public static class OperationWire
{
	public static string ToWire(OperationKind kind) => kind switch
	{
		OperationKind.Mods => "mods",
		OperationKind.Game => "game",
		OperationKind.GameRollback => "game_rollback",
		OperationKind.Provision => "instance",
		_ => "preflight",
	};

	public static string ToWire(OperationStatus status) => status switch
	{
		OperationStatus.Running => "running",
		OperationStatus.Success => "success",
		OperationStatus.Failed => "failed",
		_ => "interrupted",
	};

	public static string ToWire(OperationPhase phase) => phase switch
	{
		OperationPhase.Mods => "mods",
		OperationPhase.Status => "status",
		OperationPhase.PlayerAction => "player-action",
		OperationPhase.Service => "service",
		OperationPhase.BackupCreateOffline => "backup-create-offline",
		OperationPhase.BackupInspect => "backup-inspect",
		OperationPhase.BackupRestore => "backup-restore",
		OperationPhase.ModInstall => "mod-install",
		OperationPhase.GamePackageInspect => "game-package-inspect",
		OperationPhase.GameUpdateApply => "game-update-apply",
		OperationPhase.GameUpdateRollback => "game-update-rollback",
		OperationPhase.Catalogue => "catalogue",
		OperationPhase.Download => "download",
		OperationPhase.Create => "create",
		OperationPhase.Start => "start",
		OperationPhase.Ready => "ready",
		OperationPhase.Rollback => "rollback",
		_ => "panel-game-version",
	};

	public static string ToWire(StepAction action) => action switch
	{
		StepAction.Validate => "validate",
		StepAction.Download => "download",
		StepAction.Backup => "backup",
		StepAction.Install => "install",
		StepAction.Version => "version",
		StepAction.Rollback => "rollback",
		StepAction.RollbackData => "rollback_data",
		StepAction.Catalogue => "catalogue",
		StepAction.Create => "create",
		StepAction.Service => "service",
		StepAction.Start => "start",
		StepAction.Ready => "ready",
		_ => "restart_after_failed_rollback",
	};

	public static string ToWire(StepResult result) => result switch
	{
		StepResult.Ok => "ok",
		StepResult.Failed => "failed",
		StepResult.Warning => "warning",
		_ => "skipped",
	};

	/// <summary>Заголовок записи.</summary>
	public static string TitleOf(OperationKind kind) => kind switch
	{
		OperationKind.Mods => "Обновление модов",
		OperationKind.Game => "Обновление Vintage Story Server",
		OperationKind.GameRollback => "Откат версии сервера",
		OperationKind.ProvisionPreflight => "Сервер не создан",
		_ => "Обновление не началось",
	};
}

/// <summary>Имя снимка копии данных или программы; только имя файла, без пути.</summary>
public readonly record struct SnapshotName
{
	private readonly string? value;

	private SnapshotName(string value) => this.value = value;

	public string Value => value ?? string.Empty;

	public static Result<SnapshotName> Create(string? raw)
	{
		var value = (raw ?? string.Empty).Trim();
		return value.Length is > 0 and <= 255 && PlainFileName.IsPlain(value)
			? new SnapshotName(value)
			: Failure.Invalid("Недопустимое имя снимка");
	}

	public override string ToString() => Value;
}

/// <summary>
/// Шаг: <c>{action, status, …}</c>. Набор полей зависит от действия, и интерфейс читает их по
/// действию.
/// </summary>
public sealed record OperationStep(StepAction Action, StepResult Result)
{
	public int? Count { get; init; }

	public long? Files { get; init; }

	public string? Name { get; init; }

	public string? ModId { get; init; }

	public string? Version { get; init; }

	public string? Snapshot { get; init; }

	public string? GameVersion { get; init; }

	public string? Detail { get; init; }

	/// <summary>Размер пакета по каталогу; шаг <c>catalogue</c> создания сервера.</summary>
	public long? Size { get; init; }

	/// <summary>Скачано байт; шаг <c>download</c> создания сервера.</summary>
	public long? Bytes { get; init; }

	public string? Root { get; init; }

	public string? TelemetryMod { get; init; }

	public string? Unit { get; init; }

	public JsonObject ToJson()
	{
		var step = new JsonObject
		{
			["action"] = OperationWire.ToWire(Action),
			["status"] = OperationWire.ToWire(Result),
		};

		switch (Action)
		{
			case StepAction.Download when Bytes is { } bytes:
				// Пакет сервера: этот шаг пишется только после сверки суммы с каталогом.
				step["bytes"] = bytes;
				step["md5"] = "verified";
				break;
			case StepAction.Download:
				step["count"] = Count ?? 0;
				break;
			case StepAction.Catalogue:
				step["version"] = Version;
				step["size"] = Size ?? 0;
				break;
			case StepAction.Create:
				step["root"] = Root ?? string.Empty;
				step["telemetry_mod"] = TelemetryMod ?? string.Empty;
				break;
			case StepAction.Service:
				step["unit"] = Unit ?? string.Empty;
				break;
			case StepAction.Validate:
				step["files"] = Files;
				break;
			case StepAction.Backup or StepAction.RollbackData:
				step["name"] = Name;
				break;
			case StepAction.Install when Snapshot is null:
				// Установка мода: modid и version пишутся и пустыми.
				step["modid"] = ModId;
				step["version"] = Version;
				break;
			case StepAction.Version:
				step["game_version"] = GameVersion;
				break;
		}

		if (Snapshot is not null)
		{
			step["snapshot"] = Snapshot;
		}

		if (Detail is not null)
		{
			step["detail"] = Detail;
		}

		return step;
	}
}

/// <summary>
/// Долгая операция над сервером: обновление модов, игры, откат. Запись <c>current</c> в
/// <c>updates.json</c> и строка <c>update-runs.jsonl</c>.
/// </summary>
/// <remarks>
/// <para>
/// Детализация Maintenance §3. Шаги только добавляются, итог выставляется один раз, и
/// прерванной может стать только незавершённая операция. Запись, которая завершилась, но не
/// успела убрать себя из <c>current</c>, при старте панели прерванной не станет.
/// </para>
/// <para>
/// Этап (<see cref="Phase"/>) — последняя вызванная операция демона, а не последний шаг:
/// шаги записываются после вызова.
/// </para>
/// </remarks>
public sealed class Operation
{
	private readonly List<OperationStep> steps = [];

	private Operation(OperationId id, OperationKind kind, long startedAt)
	{
		Id = id;
		Kind = kind;
		StartedAt = startedAt;
	}

	public OperationId Id { get; }

	public OperationKind Kind { get; }

	public string Title => Kind == OperationKind.Provision
		? $"Создание сервера {Instance?.Value}"
		: OperationWire.TitleOf(Kind);

	/// <summary>Создаваемый сервер; пока операция идёт, его нет в реестре.</summary>
	public InstanceId? Instance { get; private init; }

	/// <summary>Корень дерева созданного сервера; появляется вместе с шагом <c>create</c>.</summary>
	public string? Root { get; private set; }

	/// <summary>Вердикт готовности после запуска созданного сервера.</summary>
	public JsonObject? Health { get; private set; }

	public OperationFamily Family => Kind is OperationKind.Provision or OperationKind.ProvisionPreflight
		? OperationFamily.Provision
		: OperationFamily.Updates;

	public long StartedAt { get; }

	public OperationStatus Status { get; private set; } = OperationStatus.Running;

	public OperationPhase? Phase { get; private set; }

	public string? Detail { get; private set; }

	public long? FinishedAt { get; private set; }

	public string? PreviousVersion { get; private init; }

	public string? TargetVersion { get; private init; }

	public SnapshotName? DataSnapshot { get; private set; }

	public SnapshotName? ProgramSnapshot { get; private set; }

	public bool? PanelRestartRequired { get; private set; }

	public IReadOnlyList<OperationStep> Steps => steps;

	public bool IsTerminal => Status is not OperationStatus.Running;

	public static Operation Start(OperationId id, OperationKind kind, long startedAt)
	{
		if (kind is OperationKind.Game or OperationKind.Provision)
		{
			throw new ArgumentException("Обновление игры и создание сервера создаются своими фабриками", nameof(kind));
		}

		return new Operation(id, kind, startedAt);
	}

	/// <summary>Обновление игры: исходная и целевая версии. Откат возвращает исходную.</summary>
	public static Operation CreateGameUpdate(OperationId id, string previousVersion, string targetVersion, long startedAt) =>
		new(id, OperationKind.Game, startedAt) { PreviousVersion = previousVersion, TargetVersion = targetVersion };

	/// <summary>Создание сервера: какой сервер и какая версия.</summary>
	public static Operation CreateProvision(OperationId id, InstanceId instance, string targetVersion, long startedAt) =>
		new(id, OperationKind.Provision, startedAt) { Instance = instance, TargetVersion = targetVersion };

	/// <summary>
	/// Шаг создания сервера: записать его и сделать текущим этапом.
	/// </summary>
	public void Advance(OperationStep step)
	{
		ArgumentNullException.ThrowIfNull(step);
		Record(step);
		if (step.Action == StepAction.Create && step.Root is not null)
		{
			Root = step.Root;
		}

		Phase = step.Action switch
		{
			StepAction.Catalogue => OperationPhase.Catalogue,
			StepAction.Download => OperationPhase.Download,
			StepAction.Create => OperationPhase.Create,
			StepAction.Service => OperationPhase.Service,
			StepAction.Start => OperationPhase.Start,
			StepAction.Ready => OperationPhase.Ready,
			StepAction.Rollback => OperationPhase.Rollback,
			_ => Phase,
		};
	}

	public void NoteHealth(JsonObject health)
	{
		ArgumentNullException.ThrowIfNull(health);
		EnsureRunning();
		Health = (JsonObject) health.DeepClone();
	}

	public void Enter(OperationPhase phase)
	{
		EnsureRunning();
		Phase = phase;
	}

	public void Record(OperationStep step)
	{
		ArgumentNullException.ThrowIfNull(step);
		EnsureRunning();
		steps.Add(step);
	}

	public void NoteDataSnapshot(SnapshotName name)
	{
		EnsureRunning();
		DataSnapshot = name;
	}

	public void NoteProgramSnapshot(SnapshotName name)
	{
		EnsureRunning();
		ProgramSnapshot = name;
	}

	public void NotePanelRestart(bool required)
	{
		EnsureRunning();
		PanelRestartRequired = required;
	}

	/// <summary>Итог при успехе; для модов с числом установленных.</summary>
	public void Succeed(long finishedAt) => Finish(OperationStatus.Success, Kind switch
	{
		OperationKind.Mods => $"Обновлено модов: {steps.Count(step => step.Action == StepAction.Install)}",
		OperationKind.Game => "Пакет сервера установлен и проверен",
		OperationKind.Provision => ProvisionDetail(),
		_ => "Восстановлены бинарники и данные до обновления",
	}, finishedAt);

	public void Fail(Failure reason, long finishedAt)
	{
		ArgumentNullException.ThrowIfNull(reason);
		Finish(OperationStatus.Failed, reason.Message, finishedAt);
	}

	/// <summary>Панель остановилась посреди операции.</summary>
	public void MarkInterrupted(long finishedAt) =>
		Finish(OperationStatus.Interrupted, OperationRecovery.DetailFor(Family), finishedAt);

	/// <summary>Если сервер готов, «отвечает»; иначе «создан, но готовность не подтверждена» с причиной.</summary>
	private string ProvisionDetail()
	{
		var state = Health?["state"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
		if (state == "ready")
		{
			return "Сервер создан, запущен и отвечает";
		}

		var reason = Health?["detail"] is JsonValue detail && detail.TryGetValue<string>(out var said) && said.Length > 0
			? said
			: "нет данных";
		return "Сервер создан и запущен, но готовность подтвердить не удалось: " + reason;
	}

	private void Finish(OperationStatus status, string detail, long finishedAt)
	{
		if (IsTerminal)
		{
			throw new InvalidOperationException("Операция уже завершена");
		}

		Status = status;
		Detail = detail;
		FinishedAt = finishedAt;
	}

	private void EnsureRunning()
	{
		if (IsTerminal)
		{
			throw new InvalidOperationException("Операция уже завершена");
		}
	}

	/// <summary>Запись операции; <c>ts</c> — время начала, по нему обрезается история.</summary>
	public JsonObject ToJson()
	{
		var record = new JsonObject
		{
			["id"] = Id.ToString(),
			["type"] = OperationWire.ToWire(Kind),
		};

		if (Kind == OperationKind.Provision)
		{
			// Порядок ключей записи создания: instance до title, target_version после.
			record["instance"] = Instance?.Value;
			record["title"] = Title;
			record["target_version"] = TargetVersion;
		}
		else
		{
			record["title"] = Title;
		}

		record["started_at"] = StartedAt;
		record["status"] = OperationWire.ToWire(Status);
		record["steps"] = new JsonArray([.. steps.Select(step => (JsonNode?) step.ToJson())]);

		if (Kind == OperationKind.Game)
		{
			record["previous_version"] = PreviousVersion;
			record["target_version"] = TargetVersion;
		}

		if (Phase is { } phase)
		{
			record["phase"] = OperationWire.ToWire(phase);
		}

		if (DataSnapshot is { } data)
		{
			record["data_snapshot"] = data.Value;
		}

		if (ProgramSnapshot is { } program)
		{
			record["program_snapshot"] = program.Value;
		}

		if (PanelRestartRequired is { } restart)
		{
			record["panel_restart_required"] = restart;
		}

		if (Root is not null)
		{
			record["root"] = Root;
		}

		if (Health is not null)
		{
			record["health"] = Health.DeepClone();
		}

		if (FinishedAt is { } finished)
		{
			record["detail"] = Detail;
			record["finished_at"] = finished;
			record["ts"] = StartedAt;
		}

		return record;
	}
}

/// <summary>Восстановление записи, оставшейся в <c>current</c> после остановки панели.</summary>
public static class OperationRecovery
{
	public const string InterruptedDetail =
		"Панель остановилась во время операции. Проверьте сервер и резервные копии перед повтором.";

	/// <summary>Демон уже не может сам убрать недостроенное дерево.</summary>
	public const string InterruptedProvisionDetail =
		"Панель остановилась во время создания сервера. Проверьте каталог сервера и реестр перед повторной попыткой.";

	public static string DetailFor(OperationFamily family) =>
		family == OperationFamily.Provision ? InterruptedProvisionDetail : InterruptedDetail;

	/// <summary>
	/// Прерванная запись для истории или <c>null</c>, если запись уже завершена: она попала в
	/// историю, но панель остановилась, не очистив <c>current</c>. Неизвестные поля сохраняются,
	/// потому что запись могла оставить другая версия панели.
	/// </summary>
	public static JsonObject? Interrupt(JsonObject current, long now, OperationFamily family = OperationFamily.Updates)
	{
		ArgumentNullException.ThrowIfNull(current);

		if (current["status"] is JsonValue status && status.TryGetValue<string>(out var text)
			&& text is "success" or "failed" or "interrupted")
		{
			return null;
		}

		var record = (JsonObject) current.DeepClone();
		var started = Seconds(current["started_at"]) is { } seconds and not 0 ? seconds : now;

		record["status"] = "interrupted";
		record["detail"] = DetailFor(family);
		record["finished_at"] = now;
		record["ts"] = started;
		return record;
	}

	/// <summary><c>int(value)</c> для числа из JSON, целого или дробного; иначе <c>null</c>.</summary>
	internal static long? Seconds(JsonNode? node) =>
		node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number
		&& double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
			? (long) number
			: null;
}
