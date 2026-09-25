using Panel.Catalogues.Domain;

namespace Panel.Catalogues.Tests;

/// <summary>
/// Три разбора версии и их расхождения (детализация Catalogues §1). Каждое расхождение ниже —
/// зафиксированное поведение.
/// </summary>
public sealed class VersionTests
{
	/// <summary>
	/// Столбцы: версия зависимости, ветка тега ModDB и номер пакета в манифесте. Строки: набор из
	/// детализации и строки, на которых разборы расходятся.
	/// </summary>
	[Theory]
	[InlineData("1.20", true, true, false)]          // манифест требует три числа
	[InlineData("1.20.0", true, true, true)]
	[InlineData("1.20.0.1", false, false, false)]    // четвёртое число не принимает никто
	[InlineData("v1.20.0", true, true, false)]       // префикс v — только у модов и тегов
	[InlineData("1.20.0-rc.1", true, true, true)]
	[InlineData("1.20.0-rc1", true, true, true)]
	[InlineData("1.20.0_rc1", false, false, false)]
	[InlineData("1.20.0-pre.3+build", true, true, false)] // '+' в манифесте не бывает
	[InlineData("1.20.0-dev", true, true, true)]
	[InlineData("1.20.0-hotfix", false, true, true)] // ветка терпит любой суффикс, зависимость — нет
	[InlineData("V1.20.0", true, true, false)]       // регистр префикса: IGNORECASE
	[InlineData("", false, false, false)]
	[InlineData("*", false, false, false)]
	[InlineData("any", false, false, false)]
	public void Три_разбора_принимают_разное(string text, bool dependency, bool branch, bool manifest)
	{
		Assert.Equal(dependency, ModVersion.Parse(text) is not null);
		Assert.Equal(branch, VersionBranch.Parse(text) is not null);
		Assert.Equal(manifest, ManifestVersion.Parse(text) is not null);
	}

	/// <summary>
	/// Предрелиз тоже определяется тремя правилами: ранг зависимости, <c>moddb.PRERELEASE</c> для
	/// релиза мода и <c>catalogue.PRERELEASE_RE</c> для канала пакета.
	/// </summary>
	[Theory]
	[InlineData("1.20.0", false, false)]
	[InlineData("1.20.0-rc.1", true, true)]
	[InlineData("1.20.0-dev", true, true)]
	[InlineData("1.20.0-hotfix", false, false)]
	[InlineData("1.20.0-prefix", false, true)]   // канал ищет маркер без правой границы
	[InlineData("1.20.0_rc1", true, false)]      // «_» разделяет только у ModDB
	public void Предрелиз_релиза_мода_и_канал_пакета(string text, bool modRelease, bool manifestUnstable)
	{
		Assert.Equal(modRelease, new ModRelease(1, text, Compatibility.None).IsPrerelease);
		Assert.Equal(manifestUnstable, ManifestVersion.Parse(text) is { } version ? version.IsUnstable : manifestUnstable);
	}

	[Fact]
	public void Порядок_версий_совпадает_с_порядком_выпусков()
	{
		Assert.True(ModVersion.Parse("1.22.0-dev.1") < ModVersion.Parse("1.22.0-pre.1"));
		Assert.True(ModVersion.Parse("1.22.0-pre.2") < ModVersion.Parse("1.22.0-rc.1"));
		Assert.True(ModVersion.Parse("1.22.0-rc.5") < ModVersion.Parse("1.22.0"));
		Assert.True(ModVersion.Parse("v1.22.7") > ModVersion.Parse("1.22.6"));
		Assert.Equal(ModVersion.Parse("1.22"), ModVersion.Parse("1.22.0"));
	}

	[Fact]
	public void Релиз_пакета_сортируется_после_своих_предрелизов()
	{
		static ManifestVersion V(string text) => ManifestVersion.Parse(text)!.Value;

		Assert.True(V("1.22.0").CompareTo(V("1.22.0-rc.1")) > 0);
		Assert.True(V("1.22.0-rc.1").CompareTo(V("1.21.9")) > 0);
		Assert.True(V("1.22.10").CompareTo(V("1.22.9")) > 0);
	}

	[Fact]
	public void Ветка_сравнивается_как_текст()
	{
		Assert.Equal(VersionBranch.Parse("1.22.3"), VersionBranch.Parse("v1.22-rc"));
		Assert.NotEqual(VersionBranch.Parse("01.22"), VersionBranch.Parse("1.22"));
	}
}
