using System.Text;
using System.Text.Json.Nodes;
using Panel.Configuration.Domain;
using Panel.Configuration.Ports;
using Panel.Contracts.Configuration;
using Panel.Contracts.Http;
using Panel.Observability.Domain;
using Panel.Runtime.Domain;
using Panel.Runtime.Ports;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Сценарии настроек сервера: чтение с трекером, предпросмотр, сохранение с перезапуском,
/// откат, шаблон мира.
/// </summary>
/// <remarks>
/// <para>
/// Находится в композиционном корне, потому что соединяет Configuration и Runtime: трекеру
/// нужна идентичность процесса, а сохранение перезапускает службу. Правил здесь нет: решения
/// принимают <see cref="ServerConfiguration"/>, <see cref="ApplyTracker"/> и агрегат сервера в
/// <see cref="ServerControlHandler"/>.
/// </para>
/// <para>
/// Порядок шагов важен: состояние службы снимается до записи, иначе трекер запомнит процесс,
/// который сейчас будет перезапущен, и настройки навсегда останутся «ждущими перезапуска».
/// </para>
/// </remarks>
public sealed class ServerConfigurationHandler
{
	/// <summary>Предел промежуточного файла. Документ не больше 2 МБ, но отступы увеличивают размер.</summary>
	private const int stagingLimitMegabytes = 8;

	private readonly IServerConfigStore store;
	private readonly IApplyMarkerStore markers;
	private readonly IServerRuntime runtime;
	private readonly ServerControlHandler control;
	private readonly StagingWriter staging;
	private readonly TimeProvider clock;
	private readonly EventBus events;

	public ServerConfigurationHandler(
		IServerConfigStore store,
		IApplyMarkerStore markers,
		IServerRuntime runtime,
		ServerControlHandler control,
		StagingWriter staging,
		TimeProvider clock,
		EventBus events)
	{
		this.store = store;
		this.markers = markers;
		this.runtime = runtime;
		this.control = control;
		this.staging = staging;
		this.clock = clock;
		this.events = events;
	}

	/// <summary><c>GET /api/config</c>. Пароль скрыт от тех, кто не может его менять.</summary>
	public async Task<Result<ConfigDescriptionResponse>> DescribeAsync(
		InstanceId instance,
		bool redact,
		CancellationToken ct)
	{
		var current = await LoadAsync(instance, ct).ConfigureAwait(false);
		if (!current.IsSuccess)
		{
			return Result.Fail<ConfigDescriptionResponse>(current.Failure!);
		}

		var state = await InspectAsync(instance, current.Value, ct).ConfigureAwait(false);
		if (!state.IsSuccess)
		{
			return Result.Fail<ConfigDescriptionResponse>(state.Failure!);
		}

		var backups = await store.ListBackupsAsync(instance, ct).ConfigureAwait(false);
		if (!backups.IsSuccess)
		{
			return Result.Fail<ConfigDescriptionResponse>(backups.Failure!);
		}

		var config = current.Value.Configuration;
		return new ConfigDescriptionResponse
		{
			Config = redact ? config.RedactedDocument() : config.Document,
			Revision = config.Revision.Value,
			ValidationWarning = config.ValidationWarning,
			Schema = ConfigurationMapping.Schema(),
			ApplyState = state.Value,
			Backups = backups.Value.Backups,
		};
	}

	/// <summary><c>POST /api/config/preview</c>: что изменится и нужен ли перезапуск.</summary>
	public async Task<Result<ConfigPreviewResponse>> PreviewAsync(
		InstanceId instance,
		ConfigRequest body,
		CancellationToken ct)
	{
		var plan = await PlanAsync(instance, body, ct).ConfigureAwait(false);

		return plan.Map(value => new ConfigPreviewResponse
		{
			Changes = [.. value.Changes.Select(ConfigurationMapping.Change)],
			RestartRequired = value.Changes.Any(change => change.RestartRequired),
		});
	}

	/// <summary>
	/// <c>PUT /api/config</c>. Если изменение требует перезапуска и сервер работает, он сразу
	/// перезапускается; форма предупреждает об этом администратора.
	/// </summary>
	public async Task<Result<ConfigSaveResponse>> SaveAsync(InstanceId instance, ConfigRequest body, CancellationToken ct)
	{
		var plan = await PlanAsync(instance, body, ct).ConfigureAwait(false);
		if (!plan.IsSuccess)
		{
			return Result.Fail<ConfigSaveResponse>(plan.Failure!);
		}

		var (next, changes) = plan.Value;

		if (changes.Count == 0)
		{
			var unchanged = await ReinspectAsync(instance, ct).ConfigureAwait(false);
			return unchanged.Map(state => new ConfigSaveResponse { ApplyState = state });
		}

		var service = await ObserveAsync(instance, ct).ConfigureAwait(false);
		if (!service.IsSuccess)
		{
			return Result.Fail<ConfigSaveResponse>(service.Failure!);
		}

		var written = await WriteAsync(instance, next, ct).ConfigureAwait(false);
		if (!written.IsSuccess)
		{
			await Report(instance, "config.save_failed", EventSeverity.Error,
				"Не удалось сохранить настройки", written.Failure!, ct).ConfigureAwait(false);
			return Result.Fail<ConfigSaveResponse>(written.Failure!);
		}

		await MarkAsync(instance, next, changes, service.Value, ct).ConfigureAwait(false);

		var restart = service.Value.Active && changes.Any(change => change.RestartRequired);
		if (restart)
		{
			var restarted = await control.ServiceAsync(instance, ServiceAction.Restart, ct).ConfigureAwait(false);
			if (!restarted.IsSuccess)
			{
				await Report(instance, "config.apply_failed", EventSeverity.Critical,
					"Не удалось перезапустить сервер после изменения настроек", restarted.Failure!, ct).ConfigureAwait(false);
				return Result.Fail<ConfigSaveResponse>(restarted.Failure!);
			}
		}

		var state = await ReinspectAsync(instance, ct).ConfigureAwait(false);
		return state.Map(value => new ConfigSaveResponse
		{
			Backup = written.Value.Backup,
			RestartRequired = written.Value.RestartRequired,
			AutoRestarted = restart,
			ApplyState = value,
		});
	}

	/// <summary>
	/// <c>POST /api/config/rollback</c>. Работающий сервер перезапускается всегда: копия — целый
	/// файл, и что изменится, до её чтения неизвестно.
	/// </summary>
	public async Task<Result<ConfigRollbackResponse>> RollbackAsync(
		InstanceId instance,
		ConfigBackupName backup,
		CancellationToken ct)
	{
		var before = await LoadAsync(instance, ct).ConfigureAwait(false);
		if (!before.IsSuccess)
		{
			return Result.Fail<ConfigRollbackResponse>(before.Failure!);
		}

		var service = await ObserveAsync(instance, ct).ConfigureAwait(false);
		if (!service.IsSuccess)
		{
			return Result.Fail<ConfigRollbackResponse>(service.Failure!);
		}

		var restored = await store.RollbackAsync(instance, backup, ct).ConfigureAwait(false);
		if (!restored.IsSuccess)
		{
			await Report(instance, "config.rollback_failed", EventSeverity.Error,
				"Не удалось откатить настройки", restored.Failure!, ct).ConfigureAwait(false);
			return Result.Fail<ConfigRollbackResponse>(restored.Failure!);
		}

		var after = await LoadAsync(instance, ct).ConfigureAwait(false);
		if (!after.IsSuccess)
		{
			return Result.Fail<ConfigRollbackResponse>(after.Failure!);
		}

		var changes = before.Value.Configuration.DiffTo(after.Value.Configuration);
		await MarkAsync(instance, after.Value.Configuration, changes, service.Value, ct).ConfigureAwait(false);

		if (service.Value.Active)
		{
			var restarted = await control.ServiceAsync(instance, ServiceAction.Restart, ct).ConfigureAwait(false);
			if (!restarted.IsSuccess)
			{
				await Report(instance, "config.apply_failed", EventSeverity.Critical,
					"Не удалось перезапустить сервер после отката настроек", restarted.Failure!, ct).ConfigureAwait(false);
				return Result.Fail<ConfigRollbackResponse>(restarted.Failure!);
			}
		}

		var state = await InspectAsync(instance, after.Value, ct).ConfigureAwait(false);
		return state.Map(value => new ConfigRollbackResponse
		{
			Restored = restored.Value.Restored,
			SafetyBackup = restored.Value.SafetyBackup,
			RestartRequired = restored.Value.RestartRequired,
			AutoRestarted = service.Value.Active,
			ApplyState = value,
		});
	}

	/// <summary><c>GET /api/world-config</c>: шаблон нового мира и время изменения файла как ревизия.</summary>
	public async Task<Result<WorldTemplateResponse>> ReadWorldTemplateAsync(InstanceId instance, CancellationToken ct)
	{
		var current = await LoadAsync(instance, ct).ConfigureAwait(false);

		return current.Map(value => new WorldTemplateResponse
		{
			Config = value.Configuration.WorldTemplate.Document,
			Revision = value.ModifiedNanoseconds,
		});
	}

	/// <summary>
	/// <c>PUT /api/world-config</c>. Сервер не перезапускается: шаблон действует только при
	/// создании нового мира.
	/// </summary>
	public async Task<Result<ConfigWritePayload>> SaveWorldTemplateAsync(
		InstanceId instance,
		JsonConfigRequest body,
		CancellationToken ct)
	{
		var template = WorldTemplate.FromJson(body.Config);
		if (!template.IsSuccess)
		{
			return Result.Fail<ConfigWritePayload>(template.Failure!);
		}

		var current = await LoadAsync(instance, ct).ConfigureAwait(false);
		if (!current.IsSuccess)
		{
			return Result.Fail<ConfigWritePayload>(current.Failure!);
		}

		if (body.Revision != 0 && body.Revision != current.Value.ModifiedNanoseconds)
		{
			return Failure.Conflict("serverconfig.json изменился; обновите данные");
		}

		var next = current.Value.Configuration.WithWorldTemplate(template.Value);
		if (!next.IsSuccess)
		{
			return Result.Fail<ConfigWritePayload>(next.Failure!);
		}

		var written = await WriteAsync(instance, next.Value, ct).ConfigureAwait(false);
		if (!written.IsSuccess)
		{
			return written;
		}

		var service = await ObserveAsync(instance, ct).ConfigureAwait(false);
		if (service.IsSuccess)
		{
			await MarkAsync(
					instance,
					next.Value,
					current.Value.Configuration.DiffTo(next.Value),
					service.Value,
					ct)
				.ConfigureAwait(false);
		}

		return written;
	}

	/// <summary>
	/// Общая часть предпросмотра и сохранения: ревизия, форма, схема, дифф. Ревизия проверяется
	/// первой: чужая правка важнее ошибки в своей.
	/// </summary>
	private async Task<Result<(ServerConfiguration Next, IReadOnlyList<ConfigChange> Changes)>> PlanAsync(
		InstanceId instance,
		ConfigRequest body,
		CancellationToken ct)
	{
		var current = await LoadAsync(instance, ct).ConfigureAwait(false);
		if (!current.IsSuccess)
		{
			return Failure(current.Failure!);
		}

		if (ConfigRevision.ParseOptional(body.Revision) is { } expected
			&& expected != current.Value.Configuration.Revision)
		{
			return SharedKernel.Failure.Conflict("Конфигурация была изменена после загрузки. Обновите страницу");
		}

		var next = ServerConfiguration.FromJson(body.Config);
		if (!next.IsSuccess)
		{
			return Failure(next.Failure!);
		}

		var valid = next.Value.Validate();
		if (!valid.IsSuccess)
		{
			return Failure(valid.Failure!);
		}

		return (next.Value, current.Value.Configuration.DiffTo(next.Value));

		static Result<(ServerConfiguration, IReadOnlyList<ConfigChange>)> Failure(SharedKernel.Failure failure) =>
			Result.Fail<(ServerConfiguration, IReadOnlyList<ConfigChange>)>(failure);
	}

	private async Task<Result<Loaded>> LoadAsync(InstanceId instance, CancellationToken ct)
	{
		var raw = await store.ReadAsync(instance, ct).ConfigureAwait(false);
		if (!raw.IsSuccess)
		{
			return Result.Fail<Loaded>(raw.Failure!);
		}

		return ServerConfiguration.FromJson(raw.Value.Config)
			.Map(config => new Loaded(config, raw.Value.ModifiedNanoseconds));
	}

	/// <summary>Применены ли сохранённые настройки: файл, отметка трекера и текущий процесс.</summary>
	public async Task<Result<ApplyStateResponse>> ReinspectAsync(InstanceId instance, CancellationToken ct)
	{
		var current = await LoadAsync(instance, ct).ConfigureAwait(false);

		return current.IsSuccess
			? await InspectAsync(instance, current.Value, ct).ConfigureAwait(false)
			: Result.Fail<ApplyStateResponse>(current.Failure!);
	}

	private async Task<Result<ApplyStateResponse>> InspectAsync(InstanceId instance, Loaded current, CancellationToken ct)
	{
		var service = await ObserveAsync(instance, ct).ConfigureAwait(false);
		if (!service.IsSuccess)
		{
			return Result.Fail<ApplyStateResponse>(service.Failure!);
		}

		var marker = await markers.ReadAsync(instance, ct).ConfigureAwait(false);
		var state = ApplyTracker.Inspect(marker, current.Configuration, current.ModifiedNanoseconds, service.Value);

		return ConfigurationMapping.State(state);
	}

	/// <summary>
	/// Отметка трекера. Ошибка её записи не отменяет уже записанный файл: без отметки форма
	/// покажет вывод по времени файла, а отказ после успешной записи заставил бы администратора
	/// повторить уже выполненное сохранение.
	/// </summary>
	private async Task MarkAsync(
		InstanceId instance,
		ServerConfiguration saved,
		IReadOnlyList<ConfigChange> changes,
		ServiceObservation service,
		CancellationToken ct)
	{
		var marker = ApplyTracker.Mark(saved, changes, service, clock.GetUtcNow());
		await markers.WriteAsync(instance, marker, ct).ConfigureAwait(false);
	}

	/// <summary>
	/// Процесс игры для трекера. Идентичность собирается из <see cref="ProcessIdentity"/> только
	/// здесь; строка времени старта служит запасным ключом.
	/// </summary>
	private async Task<Result<ServiceObservation>> ObserveAsync(InstanceId instance, CancellationToken ct)
	{
		var status = await runtime.StatusAsync(instance, ct).ConfigureAwait(false);

		return status.Map(snapshot => new ServiceObservation(
			snapshot.IsActive,
			snapshot.Identity.IsKnown
				? snapshot.Identity.Serialize()
				: string.IsNullOrEmpty(snapshot.StartedText) ? null : snapshot.StartedText,
			snapshot.StartedAt));
	}

	/// <summary>
	/// Документ в staging и замена файла демоном. Текст с отступом в два пробела и переводом
	/// строки в конце: этот файл открывает и правит администратор.
	/// </summary>
	private async Task<Result<ConfigWritePayload>> WriteAsync(
		InstanceId instance,
		ServerConfiguration configuration,
		CancellationToken ct)
	{
		var staged = StagingWriter.Reserve(StagedFile.Purpose.Config, ".json");
		try
		{
			var text = PythonJson.Indented(configuration.Document) + "\n";
			await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
			{
				var put = await staging.WriteAsync(stream, staged, stagingLimitMegabytes, ct).ConfigureAwait(false);
				if (!put.IsSuccess)
				{
					return Result.Fail<ConfigWritePayload>(put.Failure!);
				}
			}

			return await store.WriteAsync(instance, staged, ct).ConfigureAwait(false);
		}
		finally
		{
			staging.Discard(staged);
		}
	}

	/// <summary>Отказ, который нужно записать в ленту событий.</summary>
	private Task Report(
		InstanceId instance,
		string kind,
		EventSeverity severity,
		string title,
		Failure failure,
		CancellationToken ct) =>
		events.EmitAsync(instance, kind, severity, title, failure.Message, "configuration", ct: ct);

	private sealed record Loaded(ServerConfiguration Configuration, long ModifiedNanoseconds);
}
