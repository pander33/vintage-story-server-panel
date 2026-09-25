using Panel.Content.Domain;
using Panel.SharedKernel;

namespace Panel.Content.Tests;

/// <summary>
/// Правила набора модов: дубликаты и недостающие зависимости.
/// </summary>
public sealed class ModCatalogueTests
{
	[Fact]
	public void Два_включённых_мода_с_одним_идентификатором_дубликат()
	{
		var catalogue = ModCatalogue.Of(
		[
			Mod("survival_1.0.zip", "survival", enabled: true),
			Mod("survival_1.1.zip", "survival", enabled: true),
		]);

		Assert.Equal("survival", Assert.Single(catalogue.DuplicateIds).Value);
	}

	/// <summary>
	/// Отключённая копия дубликатом не считается, иначе предупреждение снималось бы только
	/// удалением.
	/// </summary>
	[Fact]
	public void Отключённая_копия_не_дубликат()
	{
		var catalogue = ModCatalogue.Of(
		[
			Mod("survival_1.0.zip", "survival", enabled: true),
			Mod("survival_1.1.zip", "survival", enabled: false),
		]);

		Assert.Empty(catalogue.DuplicateIds);
	}

	[Fact]
	public void Нечитаемый_мод_не_участвует_в_поиске_дубликатов()
	{
		var catalogue = ModCatalogue.Of([Broken("junk1.zip"), Broken("junk2.zip")]);

		Assert.Empty(catalogue.DuplicateIds);
	}

	/// <summary>Встроенные моды игры не считаются недостающими: их нет на диске.</summary>
	[Fact]
	public void Зависимость_от_встроенного_мода_не_считается_недостающей()
	{
		var mod = Mod("a_1.0.zip", "a", enabled: true, dependencies: new Dictionary<string, string>
		{
			["game"] = "1.20.0",
			["survival"] = "1.20.0",
		});

		Assert.Empty(ModCatalogue.Of([mod]).MissingDependenciesOf(mod));
	}

	/// <summary>
	/// Зависимость в <c>.disabled</c> считается недостающей: при запуске отключённый мод не
	/// загрузится.
	/// </summary>
	[Fact]
	public void Отключённая_зависимость_считается_недостающей()
	{
		var dependent = Mod("a_1.0.zip", "a", enabled: true, dependencies: new Dictionary<string, string>
		{
			["b"] = "1.0.0",
		});
		var catalogue = ModCatalogue.Of([dependent, Mod("b_1.0.zip", "b", enabled: false)]);

		Assert.Equal("b", Assert.Single(catalogue.MissingDependenciesOf(dependent)));
	}

	[Fact]
	public void Регистр_идентификатора_зависимости_не_имеет_значения()
	{
		var dependent = Mod("a_1.0.zip", "a", enabled: true, dependencies: new Dictionary<string, string>
		{
			["Survival"] = "1.0.0",
		});

		Assert.Empty(ModCatalogue.Of([dependent]).MissingDependenciesOf(dependent));
	}

	[Fact]
	public void Нечитаемый_мод_сортируется_по_имени_файла()
	{
		var catalogue = ModCatalogue.Of([Mod("z_1.0.zip", "z", enabled: true), Broken("aaa.zip")]);

		Assert.Equal("aaa.zip", catalogue.Mods[0].File.Value);
	}

	[Fact]
	public void Профиль_со_ссылкой_на_исчезнувший_мод_не_применяется()
	{
		var catalogue = ModCatalogue.Of([Mod("a_1.0.zip", "a", enabled: true)]);
		var profile = new ModProfile(ModProfileName.Parse("Основной"),
			[ModArchiveName.Parse("a_1.0.zip"), ModArchiveName.Parse("b_1.0.zip")]);

		var refusal = profile.CanApplyTo(catalogue);

		Assert.False(refusal.IsSuccess);
		Assert.Equal(FailureCode.Conflict, refusal.Failure!.Code);
	}

	/// <summary>
	/// Профиль может ссылаться на отключённый мод: применение профиля перемещает файлы между
	/// двумя каталогами.
	/// </summary>
	[Fact]
	public void Профиль_со_ссылкой_на_отключённый_мод_применяется()
	{
		var catalogue = ModCatalogue.Of([Mod("a_1.0.zip", "a", enabled: false)]);
		var profile = new ModProfile(ModProfileName.Parse("Основной"), [ModArchiveName.Parse("a_1.0.zip")]);

		Assert.True(profile.CanApplyTo(catalogue).IsSuccess);
	}

	private static InstalledMod Mod(
		string file,
		string id,
		bool enabled,
		IReadOnlyDictionary<string, string>? dependencies = null) =>
		new(
			ModArchiveName.Parse(file),
			enabled,
			1024,
			DateTimeOffset.UnixEpoch,
			ModId.Parse(id),
			"1.0.0",
			id,
			"server",
			dependencies ?? new Dictionary<string, string>(),
			Unreadable: false);

	private static InstalledMod Broken(string file) =>
		new(
			ModArchiveName.Parse(file),
			Enabled: true,
			1024,
			DateTimeOffset.UnixEpoch,
			default,
			string.Empty,
			string.Empty,
			string.Empty,
			new Dictionary<string, string>(),
			Unreadable: true);
}
