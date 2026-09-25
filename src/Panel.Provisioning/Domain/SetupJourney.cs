using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Panel.SharedKernel;

namespace Panel.Provisioning.Domain;

/// <summary>Шаг мастера: поле <c>step</c> в <c>setup.json</c>.</summary>
public enum SetupStep
{
	Claim,
	Access,
	Preflight,
	Version,
	Server,
	Create,
	Done,
}

public static class SetupStepWire
{
	public static string ToWire(SetupStep step) => step switch
	{
		SetupStep.Claim => "claim",
		SetupStep.Access => "access",
		SetupStep.Preflight => "preflight",
		SetupStep.Version => "version",
		SetupStep.Server => "server",
		SetupStep.Create => "create",
		_ => "done",
	};

	public static SetupStep? FromWire(string? text) => text switch
	{
		"claim" => SetupStep.Claim,
		"access" => SetupStep.Access,
		"preflight" => SetupStep.Preflight,
		"version" => SetupStep.Version,
		"server" => SetupStep.Server,
		"create" => SetupStep.Create,
		"done" => SetupStep.Done,
		_ => null,
	};
}

/// <summary>
/// Мастер установки, от свежей установки до работающего сервера.
/// </summary>
/// <remarks>
/// <para>
/// См. заметку «Мастер установки — диаграмма состояний SetupJourney». Указатель двигается
/// только вперёд (INV-47), проваленная проверка его не двигает, завершённый мастер не
/// открывается снова, а завершение записывается до запроса перезапуска панели (INV-48). За
/// последнее отвечает вызывающий, но завершить мастер может только этот тип.
/// </para>
/// <para>
/// Порядок шагов задаёт агрегат, а не тело запроса (§4.1 заметки): проверка версии не
/// принимается, пока не пройдена проверка машины, а настройки сервера — пока не выбрана
/// версия.
/// </para>
/// <para>
/// Формат <c>setup.json</c> заморожен (ADR-04), незнакомые ключи сохраняются. Ключ
/// <c>operation</c> хранит идентификатор запущенного создания (§4.2).
/// </para>
/// </remarks>
public sealed class SetupJourney
{
	private readonly JsonObject value;

	private SetupJourney(JsonObject value) => this.value = value;

	public bool IsCompleted => value["completed"] is JsonValue flag && flag.TryGetValue<bool>(out var completed) && completed;

	/// <summary>Указатель; незнакомое значение означает начало, как <c>step if step in STEPS else "claim"</c>.</summary>
	public SetupStep Step => SetupStepWire.FromWire(Text(value["step"])) ?? SetupStep.Claim;

	public long CompletedAt => InstanceSpecification.Whole(value["completed_at"]) ?? 0;

	/// <summary>Сервер, созданный установщиком до появления администратора (<c>prepared</c>).</summary>
	public InstanceId? Prepared =>
		Text(value["prepared"]) is { Length: > 0 } text && InstanceId.Create(text) is { IsSuccess: true } id && !id.Value.IsRegistry
			? id.Value
			: null;

	/// <summary>Создание сервера, запущенное из мастера, чтобы после возврата мастер знал, за чем следить.</summary>
	public Guid? Operation => Guid.TryParseExact(Text(value["operation"]), "N", out var id) ? id : null;

	public JsonObject Drafts => value["draft"] is JsonObject draft ? (JsonObject) draft.DeepClone() : new JsonObject();

	public static SetupJourney FromJson(JsonNode? node)
	{
		var value = node is JsonObject existing ? (JsonObject) existing.DeepClone() : new JsonObject();

		// Как setdefault: имеющееся значение не меняется.
		if (!value.ContainsKey("version")) value["version"] = 1;
		if (!value.ContainsKey("completed")) value["completed"] = false;
		if (!value.ContainsKey("step")) value["step"] = "claim";
		if (!value.ContainsKey("draft")) value["draft"] = new JsonObject();
		return new SetupJourney(value);
	}

	public JsonObject Draft(SetupStep step) =>
		(value["draft"] as JsonObject)?[SetupStepWire.ToWire(step)] is JsonObject draft
			? (JsonObject) draft.DeepClone()
			: new JsonObject();

	/// <summary>
	/// Администратор создан. Если установщик уже создал сервер и он действительно есть в реестре,
	/// спрашивать больше нечего и мастер закрывается (§4.4); одной строки <c>prepared</c>
	/// недостаточно. Возвращает, закрылся ли мастер.
	/// </summary>
	public Result<bool> Claim(IReadOnlySet<InstanceId> registered, long now)
	{
		ArgumentNullException.ThrowIfNull(registered);
		var open = EnsureOpen();
		if (!open.IsSuccess)
		{
			return open.Failure!;
		}

		if (Prepared is { } prepared && registered.Contains(prepared))
		{
			Complete(prepared, $"Сервер {prepared.Value} создан установщиком", now);
			return true;
		}

		MoveTo(SetupStep.Access);
		return false;
	}

	/// <summary>
	/// Администратор есть, а указатель ещё на захвате: учётная запись появилась раньше мастера.
	/// Мастер продолжается с шага «Доступ». Возвращает, сдвинулся ли указатель.
	/// </summary>
	public bool AdministratorPresent()
	{
		if (IsCompleted || Step != SetupStep.Claim)
		{
			return false;
		}

		MoveTo(SetupStep.Access);
		return true;
	}

	/// <summary>Шаг «Доступ»: четыре ключа окружения и флаг «опубликована».</summary>
	public Result<Done> RememberAccess(AccessSettings access)
	{
		ArgumentNullException.ThrowIfNull(access);
		var reached = Require(SetupStep.Access);
		if (!reached.IsSuccess)
		{
			return reached;
		}

		var draft = new JsonObject();
		foreach (var (key, value) in access.Values)
		{
			draft[key] = value;
		}

		draft["published"] = access.Published;
		Remember(SetupStep.Access, draft);
		return Done.Ok;
	}

	/// <summary>Проверка машины прошла. Проваленную проверку сюда не передают, и указатель не двигается.</summary>
	public Result<Done> RememberMachineCheck()
	{
		var reached = Require(SetupStep.Preflight);
		if (!reached.IsSuccess)
		{
			return reached;
		}

		Remember(SetupStep.Preflight, new JsonObject { ["version"] = string.Empty, ["ok"] = true });
		return Done.Ok;
	}

	/// <summary>Выбранная версия прошла проверку.</summary>
	public Result<Done> RememberVersion(ChosenVersion version)
	{
		var reached = Require(SetupStep.Version);
		if (!reached.IsSuccess)
		{
			return reached;
		}

		Remember(SetupStep.Version, new JsonObject { ["version"] = version.Text, ["ok"] = true });
		return Done.Ok;
	}

	/// <summary>Настройки сервера проверены и записаны; ничего не создано.</summary>
	public Result<Done> RememberServer(ServerSpec spec)
	{
		ArgumentNullException.ThrowIfNull(spec);
		var reached = Require(SetupStep.Server);
		if (!reached.IsSuccess)
		{
			return reached;
		}

		Remember(SetupStep.Server, spec.ToJson());
		return Done.Ok;
	}

	/// <summary>
	/// Первый сервер создал установщик (<c>app.manage provision</c>): версия и настройки
	/// записываются как ответы мастера, сервер — как <c>prepared</c>. Указатель не двигается:
	/// мастер закроется сам, когда появится администратор и сервер будет в реестре
	/// (<see cref="Claim"/>).
	/// </summary>
	public Result<Done> PrepareByInstaller(ServerSpec spec)
	{
		ArgumentNullException.ThrowIfNull(spec);
		var open = EnsureOpen();
		if (!open.IsSuccess)
		{
			return open;
		}

		if (value["draft"] is not JsonObject drafts)
		{
			drafts = new JsonObject();
			value["draft"] = drafts;
		}

		drafts[SetupStepWire.ToWire(SetupStep.Version)] = new JsonObject { ["version"] = spec.Version, ["ok"] = true };
		drafts[SetupStepWire.ToWire(SetupStep.Server)] = spec.ToJson();
		value["prepared"] = spec.Id.Value;
		return Done.Ok;
	}

	/// <summary>Описание сервера для запуска создания; если настройки не записаны, отказ.</summary>
	public Result<ServerSpec> SpecificationToCreate()
	{
		var open = EnsureOpen();
		return open.IsSuccess ? ServerSpec.FromDraft(Draft(SetupStep.Server)) : open.Failure!;
	}

	/// <summary>Создание запущено: указатель на «create» и идентификатор операции.</summary>
	public Result<Done> BeginCreation(Guid operation)
	{
		var spec = SpecificationToCreate();
		if (!spec.IsSuccess)
		{
			return spec.Failure!;
		}

		MoveTo(SetupStep.Create);
		value["operation"] = operation.ToString("N");
		return Done.Ok;
	}

	/// <summary>
	/// Мастер закрыт. Вызывается до запроса перезапуска, чтобы перезапущенная панель не застала
	/// мастер открытым.
	/// </summary>
	public Result<Done> Finish(InstanceId instance, IReadOnlySet<InstanceId> registered, long now)
	{
		ArgumentNullException.ThrowIfNull(registered);
		var open = EnsureOpen();
		if (!open.IsSuccess)
		{
			return open;
		}

		if (!registered.Contains(instance))
		{
			return Failure.Conflict("Сервер ещё не зарегистрирован: дождитесь окончания создания");
		}

		Complete(instance, $"Сервер {instance.Value} создан через мастер установки", now);
		return Done.Ok;
	}

	/// <summary>
	/// Мастер закрыт без сервера: оператор создаст его позже из панели или не создаст. Доступ к
	/// этому моменту уже записан, иначе после закрытия мастера панель нельзя было бы открыть.
	/// Ключа <c>instance</c> нет, потому что мастер сервер не создавал.
	/// </summary>
	public Result<Done> FinishWithoutServer(long now)
	{
		var reached = Require(SetupStep.Preflight);
		if (!reached.IsSuccess)
		{
			return reached.Failure!.Message == atEarlierStep
				? Failure.Conflict("Сначала настройте доступ к панели")
				: reached;
		}

		value["completed"] = true;
		value["completed_at"] = now;
		value["step"] = SetupStepWire.ToWire(SetupStep.Done);
		value.Remove("instance");
		value["detail"] = "Установка завершена без создания сервера";
		return Done.Ok;
	}

	/// <summary>
	/// Панель была настроена до появления мастера: администратор есть, и сервер, которым
	/// она управляет, есть в реестре.
	/// </summary>
	public bool Adopt(InstanceId panelInstance, bool hasAdministrator, IReadOnlySet<InstanceId> registered, long now)
	{
		ArgumentNullException.ThrowIfNull(registered);
		if (IsCompleted || !hasAdministrator || !registered.Contains(panelInstance))
		{
			return false;
		}

		Complete(panelInstance, "Панель была настроена до появления мастера установки", now);
		return true;
	}

	public JsonObject ToJson() => (JsonObject) value.DeepClone();

	private void Complete(InstanceId instance, string detail, long now)
	{
		value["completed"] = true;
		value["completed_at"] = now;
		value["step"] = SetupStepWire.ToWire(SetupStep.Done);
		value["instance"] = instance.Value;
		value["detail"] = detail;
	}

	/// <summary><c>remember</c>: записать ответы шага и передвинуть указатель на следующий, только вперёд.</summary>
	private void Remember(SetupStep step, JsonObject data)
	{
		if (value["draft"] is not JsonObject drafts)
		{
			drafts = new JsonObject();
			value["draft"] = drafts;
		}

		drafts[SetupStepWire.ToWire(step)] = data;
		MoveTo((SetupStep) Math.Min((int) step + 1, (int) SetupStep.Done));
	}

	private void MoveTo(SetupStep step)
	{
		if (step > Step)
		{
			value["step"] = SetupStepWire.ToWire(step);
		}
	}

	private Result<Done> EnsureOpen() =>
		IsCompleted ? Failure.Conflict("Мастер установки уже завершён") : Done.Ok;

	private Result<Done> Require(SetupStep step)
	{
		var open = EnsureOpen();
		if (!open.IsSuccess)
		{
			return open;
		}

		return Step >= step ? Done.Ok : Failure.Conflict(atEarlierStep);
	}

	private const string atEarlierStep = "Сначала пройдите предыдущий шаг мастера установки";

	private static string? Text(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

/// <summary>Версия, выбранная в мастере: непустая строка без пробелов по краям, как <c>body.version.strip()</c>.</summary>
public readonly record struct ChosenVersion
{
	private ChosenVersion(string text) => Text = text;

	public string Text { get; }

	public static ChosenVersion? Create(string? raw) =>
		(raw ?? string.Empty).Trim() is { Length: > 0 } text ? new ChosenVersion(text) : null;
}

/// <summary>
/// Одноразовый токен захвата, который печатает установщик, чтобы администратором не стал
/// первый, кто открыл порт.
/// </summary>
/// <remarks>
/// Сравниваются байты UTF-8 за постоянное время: вставленный токен может быть любым текстом,
/// и на неверный токен должен прийти ответ, а не исключение.
/// </remarks>
public sealed class ClaimToken
{
	private readonly byte[] stored;

	private ClaimToken(byte[] stored) => this.stored = stored;

	/// <summary>Токен из файла; пустой файл означает, что токена нет.</summary>
	public static ClaimToken? Create(string? stored) =>
		(stored ?? string.Empty).Trim() is { Length: > 0 } text ? new ClaimToken(Encoding.UTF8.GetBytes(text)) : null;

	public bool Accepts(OfferedToken offered) =>
		CryptographicOperations.FixedTimeEquals(stored, Encoding.UTF8.GetBytes(offered.Text));
}

/// <summary>Токен, введённый человеком: без пробелов по краям, возможно пустой.</summary>
public readonly record struct OfferedToken
{
	private OfferedToken(string text) => Text = text;

	public string Text { get; }

	public static OfferedToken Create(string? raw) => new((raw ?? string.Empty).Trim());
}
