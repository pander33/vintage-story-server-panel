using Panel.SharedKernel;

namespace Panel.Configuration.Domain;

/// <summary>Применены ли сохранённые настройки.</summary>
public enum ApplyStateKind
{
	Applied,
	RestartRequired,
	StartRequired,
}

/// <summary>На чём основан вывод трекера. Показывается в интерфейсе.</summary>
public enum ApplySource
{
	/// <summary>По отметке, оставленной панелью при сохранении.</summary>
	Tracked,

	/// <summary>По состоянию службы, без отметки.</summary>
	Service,

	/// <summary>По времени изменения файла: его правили в обход панели.</summary>
	FileTime,
}

public static class ApplyWire
{
	public static string ToWire(ApplyStateKind state) => state switch
	{
		ApplyStateKind.Applied => "applied",
		ApplyStateKind.RestartRequired => "restart_required",
		ApplyStateKind.StartRequired => "start_required",
		_ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
	};

	public static string ToWire(ApplySource source) => source switch
	{
		ApplySource.Tracked => "tracked",
		ApplySource.Service => "service",
		ApplySource.FileTime => "file_time",
		_ => throw new ArgumentOutOfRangeException(nameof(source), source, null),
	};
}

/// <summary>
/// Процесс игры с точки зрения контекста Configuration.
/// </summary>
/// <remarks>
/// <para>
/// Контекст не ссылается на Runtime (ADR-01), поэтому идентичность процесса приходит строкой,
/// той же, что записывается в <c>config-apply.json</c>. Трекер только сравнивает записанное с
/// текущим. Строка собирается в одном месте, из <c>ProcessIdentity</c> контекста Runtime
/// (INV-11).
/// </para>
/// <para>
/// Ключ процесса — <c>boot_id:pid:start_ticks</c>; если /proc не отдал тройку, то время старта
/// от systemd; если нет и его, ключа нет.
/// </para>
/// </remarks>
public sealed record ServiceObservation(bool Active, string? ProcessKey, DateTimeOffset? StartedAt)
{
	public static ServiceObservation Stopped { get; } = new(false, null, null);
}

/// <summary>
/// Отметка, оставленная при сохранении; поля те же, что в <c>config-apply.json</c>.
/// </summary>
public sealed record ApplyMarker(
	string? Revision,
	string? ServiceInstance,
	IReadOnlyList<string> ChangedKeys,
	bool? RequiresRestart,
	long? SavedAt);

/// <summary>Вывод трекера для формы настроек.</summary>
public sealed record ApplyState(
	ApplyStateKind State,
	bool Active,
	IReadOnlyList<string> ChangedKeys,
	long? SavedAt,
	ApplySource Source)
{
	public bool RestartRequired => State == ApplyStateKind.RestartRequired;
}

/// <summary>
/// Требуют ли сохранённые настройки перезапуска (INV-19).
/// </summary>
/// <remarks>
/// Игра читает <c>serverconfig.json</c> только при старте. Трекер запоминает, какой процесс
/// работал в момент сохранения: если тот же, нужен перезапуск, если другой, настройки
/// применены. Сравнивается не pid: pid переиспользуется, а ручной <c>systemctl restart</c> в
/// обход панели нужно заметить.
/// </remarks>
public static class ApplyTracker
{
	/// <summary>
	/// Запись игры в собственный файл в первые пять секунд после старта не считается правкой
	/// администратора.
	/// </summary>
	public static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(5);

	public static ApplyMarker Mark(
		ServerConfiguration saved,
		IReadOnlyList<ConfigChange> changes,
		ServiceObservation service,
		DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(saved);
		ArgumentNullException.ThrowIfNull(changes);
		ArgumentNullException.ThrowIfNull(service);

		var changedKeys = changes.Where(change => change.RestartRequired).Select(change => change.Key).ToList();

		return new ApplyMarker(
			saved.Revision.Value,
			service.Active ? service.ProcessKey : null,
			changedKeys,
			changedKeys.Count > 0,
			now.ToUnixTimeSeconds());
	}

	/// <summary>
	/// Те же ветки, что в <c>ConfigApplyTracker.inspect</c>. Порядок важен: остановленная служба
	/// проверяется раньше отметки.
	/// </summary>
	public static ApplyState Inspect(
		ApplyMarker? marker,
		ServerConfiguration current,
		long modifiedNanoseconds,
		ServiceObservation service)
	{
		ArgumentNullException.ThrowIfNull(current);
		ArgumentNullException.ThrowIfNull(service);

		var matches = marker is not null
			&& string.Equals(marker.Revision, current.Revision.Value, StringComparison.Ordinal);
		var changedKeys = matches ? marker!.ChangedKeys : [];

		ApplyStateKind state;
		ApplySource source;

		if (!service.Active)
		{
			state = ApplyStateKind.StartRequired;
			source = matches ? ApplySource.Tracked : ApplySource.Service;
		}
		else if (matches && !(marker!.RequiresRestart ?? changedKeys.Count > 0))
		{
			state = ApplyStateKind.Applied;
			source = ApplySource.Tracked;
		}
		else if (matches
				 && marker!.ServiceInstance is not null
				 && string.Equals(marker.ServiceInstance, service.ProcessKey, StringComparison.Ordinal))
		{
			state = ApplyStateKind.RestartRequired;
			source = ApplySource.Tracked;
		}
		else if (matches && service.ProcessKey is not null)
		{
			state = ApplyStateKind.Applied;
			source = ApplySource.Tracked;
		}
		else
		{
			// Файл правили в обход панели: он новее старта процесса больше чем на допуск. Без времени
			// старта сравнивать не с чем, и настройки считаются применёнными.
			var startedNanoseconds = service.StartedAt is { } started
				? (started.ToUnixTimeMilliseconds() * 1_000_000L)
				: 0;
			var external = startedNanoseconds != 0
				&& modifiedNanoseconds > startedNanoseconds + (long) StartupGrace.TotalMilliseconds * 1_000_000L;

			state = external ? ApplyStateKind.RestartRequired : ApplyStateKind.Applied;
			source = external ? ApplySource.FileTime : ApplySource.Service;
		}

		return new ApplyState(state, service.Active, changedKeys, matches ? marker!.SavedAt : null, source);
	}
}
