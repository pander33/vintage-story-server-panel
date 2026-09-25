using Panel.SharedKernel;

namespace Panel.Cli;

/// <summary>
/// Каталог сборки, из которого ставится панель: вывод <c>scripts/publish.sh</c> или уже
/// установленная версия (для <c>repair</c>).
/// </summary>
/// <remarks>
/// Три бинарника публикуются под именами сборок, а ставятся под именами из юнитов. В каталоге
/// установленной версии уже новые имена, поэтому принимаются оба варианта.
/// </remarks>
internal sealed record Artifact(string Directory, string Version, string Rid, IReadOnlyList<string> TelemetryArchives, bool Windows = false)
{
	/// <summary>Имя при публикации → имя в каталоге версии.</summary>
	public static IReadOnlyList<(string Published, string Installed)> Binaries { get; } =
	[
		("Panel.Web", "vs-panel-web"),
		("Panel.Agent", "vs-panel-agent"),
		("Panel.Cli", "vs-panel"),
	];

	public const string VersionFile = "VERSION";

	public const string RidFile = "RID";

	/// <summary>Расширение исполняемого файла; на Windows без него бинарник не запускается.</summary>
	public static string Suffix(bool windows) => windows ? ".exe" : string.Empty;

	public static Result<Artifact> Open(string directory, bool windows = false)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		var root = Path.GetFullPath(directory);
		if (!System.IO.Directory.Exists(root))
		{
			return Failure.NotFound($"Каталог не найден: {directory}");
		}

		foreach (var (published, installed) in Binaries)
		{
			if (!File.Exists(Path.Combine(root, published + Suffix(windows))) && !File.Exists(Path.Combine(root, installed + Suffix(windows))))
			{
				return Failure.Invalid(
					$"В {root} нет {published}{Suffix(windows)}: это не каталог сборки панели (bash scripts/publish.sh {(windows ? "win-x64" : "linux-x64")})");
			}
		}

		var version = ReadLine(Path.Combine(root, VersionFile));
		if (!ReleaseHistory.IsVersion(version))
		{
			return Failure.Invalid($"В {root} нет файла {VersionFile} с версией панели");
		}

		var archives = System.IO.Directory.Exists(Path.Combine(root, "telemetry"))
			? System.IO.Directory.GetFiles(Path.Combine(root, "telemetry"), "vspaneltelemetry_*.zip")
			: [];
		return new Artifact(root, version!, ReadLine(Path.Combine(root, RidFile)) ?? string.Empty, [.. archives.Order(StringComparer.Ordinal)], windows);
	}

	/// <summary>Файл бинарника в этом каталоге под именем публикации или установки.</summary>
	public string Source((string Published, string Installed) binary)
	{
		var published = Path.Combine(Directory, binary.Published + Suffix(Windows));
		return File.Exists(published) ? published : Path.Combine(Directory, Installed(binary));
	}

	/// <summary>Имя бинарника в каталоге версии.</summary>
	public string Installed((string Published, string Installed) binary) => binary.Installed + Suffix(Windows);

	/// <summary>
	/// Самый новый архив мода телеметрии. Версии сравниваются по числам, как <c>sort -V</c>:
	/// <c>0.10.0</c> новее <c>0.9.0</c>.
	/// </summary>
	public string? NewestTelemetry => TelemetryArchives
		.OrderBy(path => Path.GetFileNameWithoutExtension(path), NaturalOrder.Instance)
		.LastOrDefault();

	private static string? ReadLine(string path)
	{
		try
		{
			return File.ReadLines(path).FirstOrDefault()?.Trim();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}
}

/// <summary>Сравнение строк, в котором числа сравниваются как числа, как <c>sort -V</c>.</summary>
internal sealed class NaturalOrder : IComparer<string>
{
	public static NaturalOrder Instance { get; } = new();

	public int Compare(string? x, string? y)
	{
		x ??= string.Empty;
		y ??= string.Empty;
		int i = 0, j = 0;
		while (i < x.Length && j < y.Length)
		{
			if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
			{
				var startX = i;
				var startY = j;
				while (i < x.Length && char.IsAsciiDigit(x[i]))
				{
					i++;
				}

				while (j < y.Length && char.IsAsciiDigit(y[j]))
				{
					j++;
				}

				var left = x[startX..i].TrimStart('0');
				var right = y[startY..j].TrimStart('0');
				var byLength = left.Length.CompareTo(right.Length);
				if (byLength != 0)
				{
					return byLength;
				}

				var byDigits = string.CompareOrdinal(left, right);
				if (byDigits != 0)
				{
					return byDigits;
				}
			}
			else
			{
				var byChar = x[i].CompareTo(y[j]);
				if (byChar != 0)
				{
					return byChar;
				}

				i++;
				j++;
			}
		}

		return (x.Length - i).CompareTo(y.Length - j);
	}
}
