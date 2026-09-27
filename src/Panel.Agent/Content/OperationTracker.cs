using System.Collections.Concurrent;
using Panel.Contracts.Content;

namespace Panel.Agent.Content;

/// <summary>Названия стадий долгих операций: их читает человек, и они же служат ключом стадии.</summary>
public static class OperationStages
{
	public const string Verify = "Проверка копии";
	public const string Stop = "Остановка сервера";
	public const string Safety = "Страховочная копия текущих данных";
	public const string Checksums = "Подсчёт контрольных сумм";
	public const string Pack = "Упаковка данных";
	public const string Check = "Проверка архива";
	public const string Extract = "Распаковка копии";
	public const string Swap = "Замена данных сервера";
	public const string Extracting = "Извлечение мира";
	public const string Start = "Запуск сервера";

	public const string BackupTitle = "Создание резервной копии";
	public const string ExportTitle = "Экспорт пакета сервера";
	public const string RestoreTitle = "Восстановление копии";
	public const string RestoreWorldTitle = "Извлечение мира из копии";

	public static readonly IReadOnlyList<string> Backup = [Stop, Checksums, Pack, Check, Start];
	public static readonly IReadOnlyList<string> Export = [Stop, Checksums, Pack, Check, Start];
	public static readonly IReadOnlyList<string> Restore = [Verify, Stop, Safety, Extract, Swap, Start];
	public static readonly IReadOnlyList<string> RestoreWorld = [Verify, Extracting];
}

/// <summary>
/// Что сейчас делает демон на каждом сервере: одна запись на сервер, пока идёт долгая операция.
/// </summary>
/// <remarks>
/// Состояние только в памяти. Оно нужно ради живого показа хода, и после перезапуска демона
/// операции всё равно нет: запрос панели оборвался вместе с ним.
/// </remarks>
public sealed class OperationTracker
{
	private readonly ConcurrentDictionary<string, OperationScope> active = new(StringComparer.Ordinal);
	private readonly TimeProvider clock;

	public OperationTracker(TimeProvider clock)
	{
		ArgumentNullException.ThrowIfNull(clock);
		this.clock = clock;
	}

	/// <summary>Начать операцию. Запись убирается, когда возвращённая область освобождена.</summary>
	public OperationScope Begin(string instance, string kind, IReadOnlyList<string> stages, string title = "")
	{
		ArgumentException.ThrowIfNullOrEmpty(instance);
		var scope = new OperationScope(this, instance, kind, title, stages, clock.GetTimestamp(), clock);
		active[instance] = scope;
		return scope;
	}

	/// <summary>Ход операции сервера; при отсутствии — пустой ответ с <c>active = false</c>.</summary>
	public OperationPayload Snapshot(string instance) =>
		active.TryGetValue(instance, out var scope) ? scope.Snapshot() : new OperationPayload();

	internal void End(OperationScope scope) =>
		active.TryRemove(new KeyValuePair<string, OperationScope>(scope.Instance, scope));
}

/// <summary>Идущая операция. Пишет стадию и байты рабочий поток, читает — опрос от панели.</summary>
public sealed class OperationScope : IDisposable
{
	private readonly OperationTracker owner;
	private readonly string kind;
	private readonly string title;
	private readonly IReadOnlyList<string> stages;
	private readonly long started;
	private readonly TimeProvider clock;
	private readonly Lock gate = new();
	private int stage;
	private long? done;
	private long? total;

	internal OperationScope(
		OperationTracker owner, string instance, string kind, string title, IReadOnlyList<string> stages, long started, TimeProvider clock)
	{
		this.owner = owner;
		Instance = instance;
		this.kind = kind;
		this.title = title;
		this.stages = stages;
		this.started = started;
		this.clock = clock;
	}

	public string Instance { get; }

	/// <summary>Перейти к стадии по названию. Названия, которых в этой операции нет, пропускаются.</summary>
	public void Enter(string title)
	{
		var index = -1;
		for (var candidate = 0; candidate < stages.Count; candidate++)
		{
			if (stages[candidate] == title)
			{
				index = candidate;
				break;
			}
		}

		if (index < 0)
		{
			return;
		}

		lock (gate)
		{
			stage = index;
			done = null;
			total = null;
		}
	}

	/// <summary>Начать считать байты на текущей стадии.</summary>
	public void Measure(long totalBytes)
	{
		lock (gate)
		{
			done = 0;
			total = totalBytes;
		}
	}

	/// <summary>Прибавить обработанные байты; без <see cref="Measure"/> ничего не делает.</summary>
	public void Advance(long bytes)
	{
		lock (gate)
		{
			if (done is { } current)
			{
				done = current + bytes;
			}
		}
	}

	internal OperationPayload Snapshot()
	{
		lock (gate)
		{
			return new OperationPayload
			{
				Active = true,
				Kind = kind,
				Title = title,
				Stages = stages,
				Stage = stage,
				ElapsedSeconds = Math.Round(clock.GetElapsedTime(started).TotalSeconds, 1),
				DoneBytes = done is { } value && total is { } limit ? Math.Min(value, limit) : null,
				TotalBytes = total,
			};
		}
	}

	public void Dispose() => owner.End(this);
}
