using System.Text.RegularExpressions;

namespace Panel.Cli;

/// <summary>
/// Какие версии панели были активны и в каком порядке: <c>releases.log</c>, по строке на
/// активацию, новые внизу.
/// </summary>
/// <remarks>
/// Порядок нужен, чтобы выбрать версию для отката и версии для удаления. Время изменения
/// каталогов для этого не подходит: повторная установка той же версии его сбрасывает.
/// Хранятся текущая и две предыдущие версии (§3 заметки).
/// </remarks>
internal sealed partial class ReleaseHistory
{
	public const int KeepPrevious = 2;

	private readonly List<string> activations;

	private ReleaseHistory(List<string> activations) => this.activations = activations;

	/// <summary>От старых к новым, без повторов.</summary>
	public IReadOnlyList<string> Activations => this.activations;

	public static ReleaseHistory Parse(string? text) =>
		new([.. Dedupe((text ?? string.Empty)
			.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
			.Where(IsVersion))]);

	/// <summary>Годится ли имя версии для имени каталога: без разделителей, «..» и пробелов.</summary>
	public static bool IsVersion(string? text) => text is not null && VersionPattern.IsMatch(text);

	/// <summary>Версия стала активной: она переносится в конец, прежнее упоминание убирается.</summary>
	public ReleaseHistory Activate(string version)
	{
		if (!IsVersion(version))
		{
			throw new ArgumentException($"Invalid version name: {version}", nameof(version));
		}

		return new ReleaseHistory([.. this.activations.Where(item => item != version), version]);
	}

	/// <summary>Версия для отката: последняя активная до текущей из ещё установленных.</summary>
	public string? Previous(string current, IReadOnlyCollection<string> installed)
	{
		ArgumentNullException.ThrowIfNull(installed);
		return this.activations.LastOrDefault(item => item != current && installed.Contains(item));
	}

	/// <summary>
	/// Лишние версии: всё установленное, кроме текущей и <see cref="KeepPrevious"/> последних
	/// активных до неё. Версии, которых нет в журнале, считаются самыми старыми.
	/// </summary>
	public IReadOnlyList<string> Obsolete(IReadOnlyCollection<string> installed, string current, int keep = KeepPrevious)
	{
		ArgumentNullException.ThrowIfNull(installed);
		var kept = this.activations
			.Where(item => item != current && installed.Contains(item))
			.Reverse()
			.Take(keep)
			.ToHashSet(StringComparer.Ordinal);

		return [.. installed.Where(item => item != current && !kept.Contains(item)).Order(StringComparer.Ordinal)];
	}

	public string Serialize() => string.Concat(this.activations.Select(item => item + "\n"));

	private static IEnumerable<string> Dedupe(IEnumerable<string> items)
	{
		var list = items.ToList();
		for (var index = 0; index < list.Count; index++)
		{
			if (list.LastIndexOf(list[index]) == index)
			{
				yield return list[index];
			}
		}
	}

	[GeneratedRegex(@"\A[0-9A-Za-z][0-9A-Za-z.+_-]{0,63}\z", RegexOptions.CultureInvariant)]
	private static partial Regex VersionPattern { get; }
}
