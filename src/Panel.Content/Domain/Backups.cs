using Panel.SharedKernel;

namespace Panel.Content.Domain;

/// <summary>Резервная копия на диске.</summary>
public sealed record BackupFile(BackupName Name, long Size, DateTimeOffset Modified);

/// <summary>Содержимое копии, полученное чтением всего архива.</summary>
/// <param name="Files">Сколько записей в архиве.</param>
/// <param name="UnpackedSize">Размер после распаковки.</param>
/// <param name="Worlds">Миры внутри: имя записи и имя файла.</param>
public sealed record BackupContents(int Files, long UnpackedSize, IReadOnlyList<BackupWorld> Worlds);

/// <summary>Мир внутри резервной копии.</summary>
/// <param name="Name">Имя файла мира.</param>
/// <param name="Member">Полное имя записи в архиве, по нему мир извлекается.</param>
/// <param name="Size">Размер после распаковки.</param>
public sealed record BackupWorld(string Name, string Member, long Size);

/// <summary>
/// Политика удержания резервных копий.
/// </summary>
/// <remarks>
/// Нижняя граница в две копии нужна, чтобы удаление по расписанию не оставило единственную
/// непроверенную копию.
/// </remarks>
public readonly record struct RetentionPolicy
{
	public const int MinimumKeepCount = 2;
	public const int MaximumKeepCount = 500;
	public const int MinimumKeepDays = 1;
	public const int MaximumKeepDays = 3650;
	public const int MaximumFreeGigabytes = 100_000;

	private RetentionPolicy(int keepCount, int keepDays, int minimumFreeGigabytes)
	{
		KeepCount = keepCount;
		KeepDays = keepDays;
		MinimumFreeGigabytes = minimumFreeGigabytes;
	}

	/// <summary>Сколько свежих копий остаётся независимо от возраста.</summary>
	public int KeepCount { get; }

	/// <summary>Моложе скольких дней копия не удаляется независимо от количества.</summary>
	public int KeepDays { get; }

	/// <summary>Сколько свободного места считается достаточным; ноль — не следить.</summary>
	public int MinimumFreeGigabytes { get; }

	public long MinimumFreeBytes => (long) MinimumFreeGigabytes * 1024 * 1024 * 1024;

	public static Result<RetentionPolicy> Create(int keepCount, int keepDays, int minimumFreeGigabytes)
	{
		if (keepCount is < MinimumKeepCount or > MaximumKeepCount)
		{
			return Failure.Invalid($"Хранить нужно от {MinimumKeepCount} до {MaximumKeepCount} копий");
		}

		if (keepDays is < MinimumKeepDays or > MaximumKeepDays)
		{
			return Failure.Invalid($"Срок хранения — от {MinimumKeepDays} до {MaximumKeepDays} дней");
		}

		return minimumFreeGigabytes is >= 0 and <= MaximumFreeGigabytes
			? new RetentionPolicy(keepCount, keepDays, minimumFreeGigabytes)
			: Failure.Invalid("Недопустимый порог свободного места");
	}
}

/// <summary>Что политика удержания сделает или сделала бы.</summary>
/// <param name="Candidates">Копии под удаление.</param>
/// <param name="ReclaimableBytes">Сколько освободится.</param>
/// <param name="FreeBefore">Свободно сейчас.</param>
/// <param name="TargetFreeBytes">Сколько должно быть свободно по политике.</param>
public sealed record RetentionPlan(
	IReadOnlyList<BackupFile> Candidates,
	long ReclaimableBytes,
	long FreeBefore,
	long TargetFreeBytes)
{
	public long FreeAfter => FreeBefore + ReclaimableBytes;

	public bool TargetMet => FreeAfter >= TargetFreeBytes;
}

/// <summary>
/// Все резервные копии сервера.
/// </summary>
/// <remarks>
/// Агрегат, потому что оба правила относятся к набору: последнюю копию удалить нельзя, а
/// после применения политики должно остаться не меньше двух.
/// </remarks>
public sealed class BackupSet
{
	private readonly List<BackupFile> backups;

	private BackupSet(List<BackupFile> backups) => this.backups = backups;

	/// <summary>Копии от новой к старой.</summary>
	public IReadOnlyList<BackupFile> Backups => backups;

	public static BackupSet Of(IEnumerable<BackupFile> backups) =>
		new([.. backups.OrderByDescending(backup => backup.Modified)]);

	public Result<BackupFile> Find(BackupName name) =>
		backups.FirstOrDefault(backup => backup.Name == name) is { } found
			? found
			: Failure.NotFound("Резервная копия не найдена");

	/// <summary>
	/// Удаление одной копии. В корзину копии не переносятся, они сами последний уровень
	/// восстановления, поэтому единственную оставшуюся удалить нельзя.
	/// </summary>
	public Result<BackupFile> CanDelete(BackupName name) =>
		Find(name).Bind(backup => backups.Count > 1
			? backup
			: Result.Fail<BackupFile>(Failure.Conflict("Последнюю резервную копию удалить нельзя")));

	/// <summary>
	/// Что удалит политика удержания. Кандидат — обычная копия, которая одновременно вышла за
	/// пределы количества и старше срока. Если бы хватало одного условия, частый график
	/// опустошал бы хранилище, а редкий не чистил бы его вовсе.
	/// </summary>
	public RetentionPlan Plan(RetentionPolicy policy, DateTimeOffset now, long freeBytes)
	{
		var cutoff = now - TimeSpan.FromDays(policy.KeepDays);

		var candidates = backups
			.Where(backup => backup.Name.IsRoutine)
			.Select((backup, index) => (backup, index))
			.Where(item => item.index >= policy.KeepCount && item.backup.Modified < cutoff)
			.Select(item => item.backup)
			.ToArray();

		return new RetentionPlan(
			candidates,
			candidates.Sum(backup => backup.Size),
			freeBytes,
			policy.MinimumFreeBytes);
	}

	/// <summary>
	/// Можно ли применить план. Проверка отделена от построения: предпросмотр показывается
	/// всегда, а выполняется только план, после которого останется из чего восстанавливаться.
	/// </summary>
	public Result<Done> CanApply(RetentionPlan plan)
	{
		if (plan.Candidates.Count == 0)
		{
			return Done.Ok;
		}

		var retained = backups
			.Where(backup => backup.Name.IsRoutine && !plan.Candidates.Contains(backup))
			.ToArray();

		return retained.Length >= RetentionPolicy.MinimumKeepCount
			? Done.Ok
			: Failure.Conflict("После очистки должно остаться не меньше двух резервных копий");
	}
}
