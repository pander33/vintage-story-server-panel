namespace Panel.Catalogues.Domain;

/// <summary>Состояние мода в плане обновления игры, поле <c>status</c>.</summary>
public enum UpgradeStatus
{
	Compatible,
	Incompatible,
	Unverified,
}

/// <summary>
/// План обновления игры: заявлена ли совместимость у точно установленных релизов модов и
/// меняется ли ветка.
/// </summary>
/// <remarks>
/// Ветка здесь вычисляется четвёртым способом, не совпадающим с тремя разборами рядом с
/// <see cref="VersionBranch"/>: первые две части до <c>-</c>, без регулярного выражения, без
/// снятия префикса <c>v</c> и без проверки, что части — числа. Правило закреплено тестом.
/// </remarks>
public static class UpgradePlan
{
	public static string BranchOf(GameVersion version)
	{
		ArgumentNullException.ThrowIfNull(version);
		var release = version.Text.Split('-', 2)[0];
		return string.Join('.', release.Split('.').Take(2));
	}

	public static bool ChangesBranch(GameVersion current, GameVersion target) =>
		BranchOf(current) != BranchOf(target);

	public static string ToWire(UpgradeStatus status) => status switch
	{
		UpgradeStatus.Compatible => "compatible",
		UpgradeStatus.Incompatible => "incompatible",
		_ => "unverified",
	};
}
