using Panel.Content.Domain;
using Panel.SharedKernel;

namespace Panel.Content.Tests;

/// <summary>
/// Правила набора резервных копий: защита последней и политика удержания.
/// </summary>
public sealed class BackupSetTests
{
	private static readonly DateTimeOffset now = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Последнюю_копию_удалить_нельзя()
	{
		var set = BackupSet.Of([Routine("2026-09-21", 0, 100)]);

		var refusal = set.CanDelete(Routine("2026-09-21", 0, 100).Name);

		Assert.False(refusal.IsSuccess);
		Assert.Equal(FailureCode.Conflict, refusal.Failure!.Code);
	}

	[Fact]
	public void Одну_из_двух_копий_удалить_можно()
	{
		var set = BackupSet.Of([Routine("a", 0, 100), Routine("b", 1, 100)]);

		Assert.True(set.CanDelete(Routine("a", 0, 100).Name).IsSuccess);
	}

	/// <summary>
	/// Кандидат должен выйти за оба предела. Старая копия, которая входит в число последних по
	/// счёту, остаётся, иначе редкий график удалял бы всё.
	/// </summary>
	[Fact]
	public void Старая_но_входящая_в_число_свежих_копия_остаётся()
	{
		var set = BackupSet.Of([Routine("a", 400, 100), Routine("b", 401, 100), Routine("c", 402, 100)]);
		var policy = RetentionPolicy.Create(3, 30, 0).Value;

		Assert.Empty(set.Plan(policy, now, 0).Candidates);
	}

	/// <summary>
	/// И наоборот: свежая копия за пределом счёта тоже остаётся, иначе частый график удалял бы
	/// сегодняшние копии.
	/// </summary>
	[Fact]
	public void Молодая_но_лишняя_по_счёту_копия_остаётся()
	{
		var set = BackupSet.Of([Routine("a", 0, 100), Routine("b", 1, 100), Routine("c", 2, 100)]);
		var policy = RetentionPolicy.Create(2, 30, 0).Value;

		Assert.Empty(set.Plan(policy, now, 0).Candidates);
	}

	[Fact]
	public void Старая_и_лишняя_копия_становится_кандидатом()
	{
		var set = BackupSet.Of([Routine("a", 0, 100), Routine("b", 1, 100), Routine("c", 400, 700)]);
		var policy = RetentionPolicy.Create(2, 30, 0).Value;

		var plan = set.Plan(policy, now, 1_000);

		Assert.Equal("vintagestory-c.tar.gz", Assert.Single(plan.Candidates).Name.Value);
		Assert.Equal(700, plan.ReclaimableBytes);
		Assert.Equal(1_700, plan.FreeAfter);
	}

	/// <summary>
	/// Страховочная копия никогда не удаляется политикой: она снята перед восстановлением на случай
	/// его неудачи.
	/// </summary>
	[Fact]
	public void Страховочная_копия_не_попадает_под_удержание()
	{
		var set = BackupSet.Of(
		[
			Routine("a", 0, 100),
			Routine("b", 1, 100),
			new BackupFile(BackupName.Parse("before-restore-old.tar.gz"), 900, now.AddDays(-400)),
		]);
		var policy = RetentionPolicy.Create(2, 30, 0).Value;

		Assert.Empty(set.Plan(policy, now, 0).Candidates);
	}

	/// <summary>
	/// План, после которого останется одна копия, не применяется, хотя для предпросмотра он
	/// строится.
	/// </summary>
	[Fact]
	public void План_оставляющий_меньше_двух_копий_не_применяется()
	{
		var set = BackupSet.Of([Routine("a", 400, 100), Routine("b", 401, 100), Routine("c", 402, 100)]);
		var everything = new RetentionPlan([.. set.Backups], 300, 0, 0);

		var refusal = set.CanApply(everything);

		Assert.False(refusal.IsSuccess);
		Assert.Equal(FailureCode.Conflict, refusal.Failure!.Code);
	}

	[Fact]
	public void План_оставляющий_две_копии_применяется()
	{
		var set = BackupSet.Of([Routine("a", 0, 100), Routine("b", 1, 100), Routine("c", 400, 100)]);
		var plan = set.Plan(RetentionPolicy.Create(2, 30, 0).Value, now, 0);

		Assert.Single(plan.Candidates);
		Assert.True(set.CanApply(plan).IsSuccess);
	}

	[Fact]
	public void Пустой_план_применяется_всегда() =>
		Assert.True(BackupSet.Of([]).CanApply(new RetentionPlan([], 0, 0, 0)).IsSuccess);

	[Theory]
	[InlineData(2, 1, 0, true)]
	[InlineData(1, 30, 0, false)]
	[InlineData(501, 30, 0, false)]
	[InlineData(3, 0, 0, false)]
	[InlineData(3, 3651, 0, false)]
	[InlineData(3, 30, -1, false)]
	[InlineData(3, 30, 100_001, false)]
	public void Пределы_политики(int keepCount, int keepDays, int freeGigabytes, bool accepted) =>
		Assert.Equal(accepted, RetentionPolicy.Create(keepCount, keepDays, freeGigabytes).IsSuccess);

	[Fact]
	public void Цель_по_свободному_месту_считается_от_плана()
	{
		var set = BackupSet.Of([Routine("a", 0, 100), Routine("b", 1, 100), Routine("c", 400, 5_000)]);
		var policy = RetentionPolicy.Create(2, 30, 1).Value;

		var plan = set.Plan(policy, now, 1024L * 1024 * 1024);

		Assert.True(plan.TargetMet);
		Assert.Equal(1024L * 1024 * 1024, plan.TargetFreeBytes);
	}

	private static BackupFile Routine(string tag, int daysOld, long size) =>
		new(BackupName.Parse($"vintagestory-{tag}.tar.gz"), size, now.AddDays(-daysOld));
}
