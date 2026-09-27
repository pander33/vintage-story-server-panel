using System.Text.RegularExpressions;

namespace Panel.Catalogues.Domain;

public enum CompatibilityKind
{
	None,

	/// <summary>Тег совпал с версией игры дословно (без префикса <c>v</c>).</summary>
	Exact,

	/// <summary>Тег из той же ветки major.minor.</summary>
	SameBranch,
}

/// <param name="MatchedTag">Тег, по которому найдена совместимость, как он записан в ModDB.</param>
public readonly record struct Compatibility(CompatibilityKind Kind, string? MatchedTag)
{
	public static Compatibility None { get; } = new(CompatibilityKind.None, null);

	public bool IsCompatible => Kind is not CompatibilityKind.None;

	/// <summary>Поле <c>compatibility</c>: <c>exact</c>, <c>same_minor</c> или <c>null</c>.</summary>
	public string? Wire => Kind switch
	{
		CompatibilityKind.Exact => "exact",
		CompatibilityKind.SameBranch => "same_minor",
		_ => null,
	};
}

/// <summary>
/// Совместимо, если тег из той же ветки major.minor; точный тег предпочтительнее. Так же
/// считает <c>moddb searchcompatible</c>.
/// </summary>
public static class CompatibilityPolicy
{
	/// <remarks>
	/// Сначала точное совпадение ищется по всем тегам, и только потом берётся первое совпадение
	/// по ветке. При одном проходе тег <c>1.22.6</c> перед <c>1.22.7</c> дал бы «та же ветка»
	/// релизу, у которого есть точный тег.
	/// </remarks>
	public static Compatibility Evaluate(IReadOnlyList<string> tags, GameVersion game)
	{
		ArgumentNullException.ThrowIfNull(tags);
		ArgumentNullException.ThrowIfNull(game);

		foreach (var tag in tags)
		{
			if (GameVersion.Unprefixed(tag) == game.Normalized)
			{
				return new Compatibility(CompatibilityKind.Exact, tag);
			}
		}

		if (game.Branch is not { } branch)
		{
			return Compatibility.None;
		}

		foreach (var tag in tags)
		{
			if (VersionBranch.Parse(tag) == branch)
			{
				return new Compatibility(CompatibilityKind.SameBranch, tag);
			}
		}

		return Compatibility.None;
	}
}

/// <summary>
/// Релиз мода в ModDB. Сырые поля для интерфейса остаются у адаптера: домену нужны только
/// идентификатор, версия и совместимость.
/// </summary>
/// <param name="Id">releaseid; <c>null</c>, если ModDB прислал не целое число.</param>
/// <param name="VersionText"><c>str(modversion or "")</c>.</param>
public sealed partial record ModRelease(long? Id, string VersionText, Compatibility Compatibility)
{
	/// <summary>
	/// Предрелиз по номеру версии, <c>PRERELEASE</c> из <c>moddb.py</c>: маркер между
	/// разделителями, чтобы <c>1.0.0-rc1</c> был предрелизом, а мод <c>prettyrc</c> нет.
	/// </summary>
	public bool IsPrerelease => Marker.IsMatch(VersionText);

	[GeneratedRegex(@"(?:^|[.+_-])(?:dev|pre|preview|rc|alpha|beta)[0-9]*(?:$|[.+_-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex Marker { get; }

	public static ModRelease From(long? id, string versionText, IReadOnlyList<string> tags, GameVersion game) =>
		new(id, versionText ?? string.Empty, CompatibilityPolicy.Evaluate(tags, game));
}

/// <summary>
/// Итог поиска релиза для обновления. Три разных причины отсутствия дают три разных
/// сообщения.
/// </summary>
public abstract record ReleaseLookup
{
	private ReleaseLookup()
	{
	}

	/// <summary>Поле <c>unavailable_reason</c>; у найденного релиза <c>null</c>.</summary>
	public abstract string? Reason { get; }

	public sealed record Found(ModRelease Release) : ReleaseLookup
	{
		public override string? Reason => null;
	}

	public sealed record NoReleases : ReleaseLookup
	{
		public override string? Reason => "no_releases";
	}

	public sealed record PrereleaseOnly : ReleaseLookup
	{
		public override string? Reason => "prerelease_only";
	}

	public sealed record NoCompatibleRelease : ReleaseLookup
	{
		public override string? Reason => "no_compatible_release";
	}

	/// <summary>
	/// Среди совместимых (и стабильных, если предрелизы не запрошены) первый с точным тегом, иначе
	/// первый в порядке ModDB, от нового к старому.
	/// </summary>
	public static ReleaseLookup Choose(IReadOnlyList<ModRelease> releases, bool includePrerelease)
	{
		ArgumentNullException.ThrowIfNull(releases);

		var eligible = releases
			.Where(release => release.Compatibility.IsCompatible && (includePrerelease || !release.IsPrerelease))
			.ToList();
		var chosen = eligible.FirstOrDefault(release => release.Compatibility.Kind is CompatibilityKind.Exact)
					 ?? eligible.FirstOrDefault();

		if (chosen is not null)
		{
			return new Found(chosen);
		}

		if (releases.Count == 0)
		{
			return new NoReleases();
		}

		return releases.Any(release => release.Compatibility.IsCompatible)
			? new PrereleaseOnly()
			: new NoCompatibleRelease();
	}

	/// <summary>
	/// Совместимый предрелиз новее стабильного выбора: ModDB перечисляет релизы от нового к старому,
	/// поэтому это предрелиз, стоящий в списке раньше выбранного стабильного (или любой, если
	/// стабильного нет). Точный тег предпочтительнее. <c>null</c> — предлагать нечего.
	/// </summary>
	public static ModRelease? NewerPrerelease(IReadOnlyList<ModRelease> releases, ReleaseLookup stable)
	{
		ArgumentNullException.ThrowIfNull(releases);
		ArgumentNullException.ThrowIfNull(stable);

		var newer = stable is Found found
			? releases.TakeWhile(release => !ReferenceEquals(release, found.Release))
			: releases;
		var eligible = newer
			.Where(release => release.Compatibility.IsCompatible && release.IsPrerelease)
			.ToList();
		return eligible.FirstOrDefault(release => release.Compatibility.Kind is CompatibilityKind.Exact)
			   ?? eligible.FirstOrDefault();
	}
}
