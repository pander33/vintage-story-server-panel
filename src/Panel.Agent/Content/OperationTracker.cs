using System.Collections.Concurrent;
using Panel.Contracts.Content;
using Panel.SharedKernel;

namespace Panel.Agent.Content;

/// <summary>Названия стадий долгих операций: их читает человек (переводит панель), и они же служат ключом стадии.</summary>
public static class OperationStages
{
	public static readonly Phrase Verify = Phrase.From("Проверка копии");
	public static readonly Phrase Stop = Phrase.From("Остановка сервера");
	public static readonly Phrase Safety = Phrase.From("Страховочная копия текущих данных");
	public static readonly Phrase Checksums = Phrase.From("Подсчёт контрольных сумм");
	public static readonly Phrase Pack = Phrase.From("Упаковка данных");
	public static readonly Phrase Check = Phrase.From("Проверка архива");
	public static readonly Phrase Extract = Phrase.From("Распаковка копии");
	public static readonly Phrase Swap = Phrase.From("Замена данных сервера");
	public static readonly Phrase Extracting = Phrase.From("Извлечение мира");
	public static readonly Phrase Start = Phrase.From("Запуск сервера");

	public static readonly Phrase BackupTitle = Phrase.From("Создание резервной копии");
	public static readonly Phrase ExportTitle = Phrase.From("Экспорт пакета сервера");
	public static readonly Phrase RestoreTitle = Phrase.From("Восстановление копии");
	public static readonly Phrase RestoreWorldTitle = Phrase.From("Извлечение мира из копии");

	public static readonly IReadOnlyList<Phrase> Backup = [Stop, Checksums, Pack, Check, Start];
	public static readonly IReadOnlyList<Phrase> Export = [Stop, Checksums, Pack, Check, Start];
	public static readonly IReadOnlyList<Phrase> Restore = [Verify, Stop, Safety, Extract, Swap, Start];
	public static readonly IReadOnlyList<Phrase> RestoreWorld = [Verify, Extracting];
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
	public OperationScope Begin(string instance, string kind, IReadOnlyList<Phrase> stages, Phrase title)
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
	private readonly Phrase title;
	private readonly IReadOnlyList<Phrase> stages;
	private readonly long started;
	private readonly TimeProvider clock;
	private readonly Lock gate = new();
	private int stage;
	private long? done;
	private long? total;

	internal OperationScope(
		OperationTracker owner, string instance, string kind, Phrase title, IReadOnlyList<Phrase> stages, long started, TimeProvider clock)
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
	public void Enter(Phrase title)
	{
		var index = -1;
		for (var candidate = 0; candidate < stages.Count; candidate++)
		{
			if (stages[candidate].Equals(title))
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
