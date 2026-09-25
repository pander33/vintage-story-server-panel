using Panel.Catalogues.Domain;

namespace Panel.Catalogues.Tests;

/// <summary>
/// Смена ветки в плане обновления игры: четвёртое, строковое правило разбора версии.
/// Расхождения с <see cref="VersionBranch"/> здесь ожидаемы.
/// </summary>
public sealed class UpgradePlanTests
{
	[Theory]
	[InlineData("1.22.7", "1.22")]
	[InlineData("1.22.8-rc.1", "1.22")]
	[InlineData("1.22", "1.22")]
	[InlineData("1", "1")]
	[InlineData("v1.22.7", "v1.22")]        // префикс остаётся: VersionBranch его снял бы
	[InlineData("1.22.7+build", "1.22")]    // «+» не разделитель, но дальше второй части не смотрят
	[InlineData("latest", "latest")]        // VersionBranch отказал бы
	public void Ветка_как_в_game_plan(string version, string branch)
	{
		Assert.Equal(branch, UpgradePlan.BranchOf(GameVersion.Create(version)));
	}

	[Fact]
	public void Смена_ветки_по_тексту()
	{
		Assert.False(UpgradePlan.ChangesBranch(GameVersion.Create("1.22.7"), GameVersion.Create("1.22.8")));
		Assert.True(UpgradePlan.ChangesBranch(GameVersion.Create("1.22.7"), GameVersion.Create("1.23.0")));
		Assert.True(UpgradePlan.ChangesBranch(GameVersion.Create("v1.22.7"), GameVersion.Create("1.22.8")));
	}
}
