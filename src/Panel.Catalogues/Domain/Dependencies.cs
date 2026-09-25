namespace Panel.Catalogues.Domain;

public enum DependencyStatus
{
	Missing,
	Outdated,
	Unverified,
}

/// <summary>Почему зависимость не удалось проверить.</summary>
public enum UnverifiedReason
{
	/// <summary>Требуемую версию нельзя разобрать.</summary>
	InvalidRequirement,

	/// <summary>Ни одна установленная версия не удовлетворяет, и среди них есть неразборчивые.</summary>
	InvalidInstalledVersion,
}

/// <summary>
/// Мод для проверки зависимостей. Отдельный тип вместо <c>InstalledMod</c> из Content:
/// контексты не ссылаются друг на друга, а проверке нужны четыре поля.
/// </summary>
/// <param name="ModId">Как в <c>modinfo.json</c>; пусто, если мод не разобран, и тогда он ничего не предоставляет.</param>
/// <param name="Version">Как в <c>modinfo.json</c>.</param>
/// <param name="Dependencies">Идентификатор зависимости → требуемая версия.</param>
public sealed record DependencySubject(
	string? ModId,
	string Version,
	bool Enabled,
	IReadOnlyDictionary<string, string> Dependencies);

/// <param name="ModId">Как записан в зависимостях мода, без приведения регистра.</param>
/// <param name="Installed">Установленные версии; у отсутствующей зависимости <c>null</c>.</param>
public sealed record DependencyIssue(
	string ModId,
	DependencyStatus Status,
	string Required,
	string? Installed,
	UnverifiedReason? Reason = null)
{
	public string StatusWire => Status switch
	{
		DependencyStatus.Missing => "missing",
		DependencyStatus.Outdated => "outdated",
		_ => "unverified",
	};

	public string? ReasonWire => Reason switch
	{
		UnverifiedReason.InvalidRequirement => "invalid_requirement",
		UnverifiedReason.InvalidInstalledVersion => "invalid_installed_version",
		_ => null,
	};
}

/// <summary>
/// Проверка зависимостей модов.
/// </summary>
/// <remarks>
/// Правила:
/// <list type="number">
/// <item><c>game</c>, <c>creative</c> и <c>survival</c> считаются установленными в версии
/// игры. Это псевдомоды, без них половина зависимостей оказалась бы отсутствующей.</item>
/// <item>Требованию <c>""</c>, <c>*</c> и <c>any</c> удовлетворяет любая версия.</item>
/// <item>Проверка никогда не блокирует. Неразборчивое требование или версия дают «не
/// проверено» с причиной: мод с плохими метаданными должен оставаться
/// устанавливаемым.</item>
/// </list>
/// Достаточно, чтобы требованию удовлетворяла любая включённая копия; отключённая
/// зависимость считается отсутствующей.
/// </remarks>
public static class DependencyPolicy
{
	private static readonly string[] builtIn = ["game", "creative", "survival"];

	private static readonly HashSet<string> anyVersion = new(["", "*", "any"], StringComparer.Ordinal);

	/// <summary>Проблемы зависимостей каждого мода, в том же порядке, что и моды.</summary>
	public static IReadOnlyList<IReadOnlyList<DependencyIssue>> Evaluate(
		IReadOnlyList<DependencySubject> mods,
		GameVersion game)
	{
		ArgumentNullException.ThrowIfNull(mods);
		ArgumentNullException.ThrowIfNull(game);

		var installed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		foreach (var builtIn in builtIn)
		{
			installed[builtIn] = [game.Text];
		}

		foreach (var mod in mods.Where(mod => mod.Enabled && !string.IsNullOrEmpty(mod.ModId)))
		{
			var key = Fold(mod.ModId!);
			if (!installed.TryGetValue(key, out var versions))
			{
				installed[key] = versions = [];
			}

			versions.Add(mod.Version);
		}

		return [.. mods.Select(mod => (IReadOnlyList<DependencyIssue>) [.. IssuesOf(mod, installed)])];
	}

	/// <summary>Отсутствующие зависимости в порядке кодов символов.</summary>
	public static IReadOnlyList<string> Missing(IReadOnlyList<DependencyIssue> issues) =>
	[
		.. issues
			.Where(issue => issue.Status is DependencyStatus.Missing)
			.Select(issue => issue.ModId)
			.Order(StringComparer.Ordinal),
	];

	private static IEnumerable<DependencyIssue> IssuesOf(
		DependencySubject mod,
		Dictionary<string, List<string>> installed)
	{
		foreach (var (dependency, requiredValue) in mod.Dependencies)
		{
			var required = (requiredValue ?? string.Empty).Trim();
			if (!installed.TryGetValue(Fold(dependency), out var versions) || versions.Count == 0)
			{
				yield return new DependencyIssue(dependency, DependencyStatus.Missing, required, null);
				continue;
			}

			if (anyVersion.Contains(Fold(required)))
			{
				continue;
			}

			var joined = string.Join(", ", versions);
			if (ModVersion.Parse(required) is not { } minimum)
			{
				yield return new DependencyIssue(
					dependency, DependencyStatus.Unverified, required, joined, UnverifiedReason.InvalidRequirement);
				continue;
			}

			var parsed = versions.Select(version => (Text: version, Version: ModVersion.Parse(version))).ToList();
			if (parsed.Any(pair => pair.Version is { } version && version >= minimum))
			{
				continue;
			}

			if (parsed.Any(pair => pair.Version is null))
			{
				yield return new DependencyIssue(
					dependency, DependencyStatus.Unverified, required, joined, UnverifiedReason.InvalidInstalledVersion);
				continue;
			}

			// MaxBy возвращает первый из равных.
			var newest = parsed.MaxBy(pair => pair.Version!.Value).Text;
			yield return new DependencyIssue(dependency, DependencyStatus.Outdated, required, newest);
		}
	}

	/// <summary>
	/// Сравнение без учёта регистра. <c>ToLowerInvariant</c> отличается от полного свёртывания
	/// регистра только на символах вроде <c>ß</c>, которых в идентификаторе мода не бывает.
	/// </summary>
	private static string Fold(string value) => value.ToLowerInvariant();
}
