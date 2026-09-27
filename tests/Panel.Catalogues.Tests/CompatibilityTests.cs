using Panel.Catalogues.Domain;

namespace Panel.Catalogues.Tests;

/// <summary>Совместимость, выбор релиза и зависимости.</summary>
public sealed class CompatibilityTests
{
	private static readonly GameVersion game = GameVersion.Create("1.22.7");

	[Fact]
	public void Точный_тег_ищется_по_всем_тегам_раньше_ветки()
	{
		var compatibility = CompatibilityPolicy.Evaluate(["1.22.6", "v1.22.7"], game);

		Assert.Equal(CompatibilityKind.Exact, compatibility.Kind);
		Assert.Equal("v1.22.7", compatibility.MatchedTag);
		Assert.Equal("exact", compatibility.Wire);
	}

	[Fact]
	public void Та_же_ветка_совместима_с_пометкой_тега()
	{
		var compatibility = CompatibilityPolicy.Evaluate(["1.21.7", "1.22.6"], game);

		Assert.Equal("same_minor", compatibility.Wire);
		Assert.Equal("1.22.6", compatibility.MatchedTag);
		Assert.False(CompatibilityPolicy.Evaluate(["1.21.7"], game).IsCompatible);
	}

	[Fact]
	public void Неразборчивая_версия_игры_совместима_только_дословно()
	{
		var latest = GameVersion.Create("latest");

		Assert.False(CompatibilityPolicy.Evaluate(["1.22.6"], latest).IsCompatible);
		Assert.True(CompatibilityPolicy.Evaluate(["latest"], latest).IsCompatible);
	}

	[Fact]
	public void Обновление_пропускает_предрелиз()
	{
		var lookup = ReleaseLookup.Choose(
			[Release("3.0.0-dev.1", CompatibilityKind.Exact), Release("2.2.0", CompatibilityKind.SameBranch)],
			includePrerelease: false);

		Assert.Equal("2.2.0", Assert.IsType<ReleaseLookup.Found>(lookup).Release.VersionText);
	}

	[Fact]
	public void Обновление_предпочитает_точный_тег_более_новому_релизу_ветки()
	{
		var lookup = ReleaseLookup.Choose(
			[Release("2.3.0", CompatibilityKind.SameBranch), Release("2.2.0", CompatibilityKind.Exact)],
			includePrerelease: false);

		Assert.Equal("2.2.0", Assert.IsType<ReleaseLookup.Found>(lookup).Release.VersionText);
	}

	[Fact]
	public void Три_разных_нет()
	{
		Assert.Equal("no_releases", ReleaseLookup.Choose([], false).Reason);
		Assert.Equal("prerelease_only", ReleaseLookup.Choose([Release("2.0.0-rc.1", CompatibilityKind.Exact)], false).Reason);
		Assert.Equal("no_compatible_release", ReleaseLookup.Choose([Release("2.0.0", CompatibilityKind.None)], false).Reason);
		Assert.IsType<ReleaseLookup.Found>(ReleaseLookup.Choose([Release("2.0.0-rc.1", CompatibilityKind.Exact)], true));
	}

	/// <summary>
	/// Предрелиз предлагается рядом со стабильным, только если он новее: ModDB перечисляет релизы
	/// от нового к старому, и предрелиз старше выбранного стабильного не нужен.
	/// </summary>
	[Fact]
	public void Предрелиз_предлагается_только_новее_стабильного()
	{
		IReadOnlyList<ModRelease> newer = [Release("2.1.0-rc.1", CompatibilityKind.Exact), Release("2.0.0", CompatibilityKind.Exact), Release("2.0.0-rc.2", CompatibilityKind.Exact)];
		IReadOnlyList<ModRelease> older = [Release("2.0.0", CompatibilityKind.Exact), Release("2.0.0-rc.2", CompatibilityKind.Exact)];
		IReadOnlyList<ModRelease> only = [Release("2.0.0-rc.1", CompatibilityKind.None), Release("1.9.0-rc.1", CompatibilityKind.SameBranch)];

		Assert.Equal("2.1.0-rc.1", ReleaseLookup.NewerPrerelease(newer, ReleaseLookup.Choose(newer, false))!.VersionText);
		Assert.Null(ReleaseLookup.NewerPrerelease(older, ReleaseLookup.Choose(older, false)));
		Assert.Equal("1.9.0-rc.1", ReleaseLookup.NewerPrerelease(only, ReleaseLookup.Choose(only, false))!.VersionText);
	}

	[Fact]
	public void Зависимости_отсутствующая_устаревшая_и_непроверенная_без_блокировки()
	{
		var mods = new[]
		{
			Mod("library", "1.2.0"),
			Mod("unknownlib", "custom"),
			Mod("consumer", "1.0.0", ("library", "1.3.0"), ("missinglib", "2.0.0"), ("unknownlib", "1.0.0"), ("game", "1.22.0")),
		};

		var issues = DependencyPolicy.Evaluate(mods, game)[2].ToDictionary(issue => issue.ModId);

		Assert.Equal(new DependencyIssue("library", DependencyStatus.Outdated, "1.3.0", "1.2.0"), issues["library"]);
		Assert.Equal(DependencyStatus.Missing, issues["missinglib"].Status);
		Assert.Null(issues["missinglib"].Installed);
		Assert.Equal(DependencyStatus.Unverified, issues["unknownlib"].Status);
		Assert.Equal("invalid_installed_version", issues["unknownlib"].ReasonWire);
		Assert.False(issues.ContainsKey("game"));
		Assert.Equal(["missinglib"], DependencyPolicy.Missing([.. issues.Values]));
	}

	[Fact]
	public void Любая_включённая_копия_удовлетворяет_зависимости()
	{
		var mods = new[] { Mod("library", "1.0.0"), Mod("library", "2.0.0"), Mod("consumer", "1.0.0", ("library", "1.5.0")) };

		Assert.Empty(DependencyPolicy.Evaluate(mods, game)[2]);
	}

	[Fact]
	public void Отключённая_зависимость_отсутствует()
	{
		var mods = new[] { Mod("library", "2.0.0") with { Enabled = false }, Mod("consumer", "1.0.0", ("library", "1.0.0")) };

		Assert.Equal(DependencyStatus.Missing, DependencyPolicy.Evaluate(mods, game)[1][0].Status);
	}

	[Theory]
	[InlineData("")]
	[InlineData("*")]
	[InlineData("ANY")]
	public void Требование_без_версии_удовлетворяет_любая(string required)
	{
		var mods = new[] { Mod("library", "custom"), Mod("consumer", "1.0.0", ("library", required)) };

		Assert.Empty(DependencyPolicy.Evaluate(mods, game)[1]);
	}

	[Fact]
	public void Неразборчивое_требование_не_проверено()
	{
		var mods = new[] { Mod("library", "1.0.0"), Mod("Consumer", "1.0.0", ("Library", ">=1.0")) };

		var issue = Assert.Single(DependencyPolicy.Evaluate(mods, game)[1]);
		Assert.Equal("invalid_requirement", issue.ReasonWire);
		Assert.Equal("Library", issue.ModId);
		Assert.Equal("1.0.0", issue.Installed);
	}

	private static ModRelease Release(string version, CompatibilityKind kind) =>
		new(1, version, kind is CompatibilityKind.None ? Compatibility.None : new Compatibility(kind, "1.22.7"));

	private static DependencySubject Mod(string id, string version, params (string Id, string Required)[] dependencies) =>
		new(id, version, true, dependencies.ToDictionary(pair => pair.Id, pair => pair.Required));
}
