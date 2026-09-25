using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Panel.Catalogues.Domain;

/// <summary>Платформа сервера. Панель ставит только серверы.</summary>
public enum ServerPlatform
{
	Linux,
	Windows,
}

/// <summary>Контрольная сумма md5 из манифеста: 32 строчные шестнадцатеричные цифры.</summary>
/// <remarks>
/// Пакет без суммы показывается, но не скачивается.
/// </remarks>
public readonly partial record struct Digest
{
	private Digest(string hex) => Hex = hex;

	public string Hex { get; }

	[GeneratedRegex(@"\A[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
	private static partial Regex Pattern { get; }

	/// <summary><c>str(md5 or "").strip().lower()</c>; <c>null</c>, если суммы нет или она не похожа на md5.</summary>
	public static Digest? Parse(string? raw)
	{
		var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
		return Pattern.IsMatch(value) ? new Digest(value) : null;
	}

	public override string ToString() => Hex;
}

/// <summary>Серверный пакет одной версии для одной платформы.</summary>
/// <param name="SizeText">Размер, как его пишет манифест: «590.5 MB».</param>
/// <param name="Size">Он же в байтах, приблизительно; нужен только для предупреждения о месте.</param>
public sealed record GamePackage(
	ManifestVersion Version,
	string FileName,
	string SizeText,
	long Size,
	Digest? Md5,
	Uri Url,
	bool Latest)
{
	public string Channel => Version.IsUnstable ? "unstable" : "stable";

	/// <summary>Элемент <c>versions</c> ответа <c>/api/catalogue</c>.</summary>
	public JsonObject ToJson() => new()
	{
		["version"] = Version.Text,
		["channel"] = Channel,
		["filename"] = FileName,
		["size_text"] = SizeText,
		["size"] = Size,
		["md5"] = Md5?.Hex ?? string.Empty,
		["url"] = Url.OriginalString,
		["latest"] = Latest,
	};
}

/// <summary>
/// Официальный манифест <c>api.vintagestory.at</c>, сведённый к серверным пакетам одной
/// платформы.
/// </summary>
/// <remarks>
/// Битая запись пропускается, чтобы один сломанный элемент не скрыл остальной каталог.
/// Хост загрузки проверяется здесь и ещё раз при скачивании.
/// </remarks>
public static partial class ReleaseManifest
{
	public static readonly IReadOnlySet<string> DownloadHosts =
		new HashSet<string>(["cdn.vintagestory.at", "account.vintagestory.at"], StringComparer.Ordinal);

	private static readonly Dictionary<string, long> sizeUnits = new(StringComparer.Ordinal)
	{
		[""] = 1,
		["K"] = 1024,
		["M"] = 1024L * 1024,
		["G"] = 1024L * 1024 * 1024,
		["T"] = 1024L * 1024 * 1024 * 1024,
	};

	[GeneratedRegex(@"\A\s*([0-9.,]+)\s*([KMGT]?)i?B\s*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex SizePattern { get; }

	public static string PlatformWire(ServerPlatform platform) =>
		platform is ServerPlatform.Windows ? "windows" : "linux";

	/// <summary>Пакеты платформы, от новых к старым.</summary>
	public static IReadOnlyList<GamePackage> Parse(JsonObject raw, ServerPlatform platform)
	{
		ArgumentNullException.ThrowIfNull(raw);

		var key = platform is ServerPlatform.Windows ? "windowsserver" : "linuxserver";
		var packages = new List<GamePackage>();

		foreach (var (name, distributions) in raw)
		{
			if (ManifestVersion.Parse(name) is not { } version
				|| distributions is not JsonObject byDistribution
				|| byDistribution[key] is not JsonObject entry
				|| TrustedUrl(entry["urls"]) is not { } url)
			{
				continue;
			}

			packages.Add(new GamePackage(
				version,
				PythonText.Of(entry["filename"]),
				PythonText.Of(entry["filesize"]),
				ParseSize(PythonText.Of(entry["filesize"])),
				Digest.Parse(PythonText.Of(entry["md5"])),
				url,
				PythonText.Truthy(entry["latest"])));
		}

		// sort(reverse=True) устойчив, но равных версий здесь нет: ключи объекта уникальны.
		return [.. packages.OrderByDescending(package => package.Version)];
	}

	/// <summary>«590.5 MB» → байты; неразборчивый размер даёт ноль.</summary>
	public static long ParseSize(string text)
	{
		var match = SizePattern.Match(text ?? string.Empty);
		if (!match.Success
			|| !double.TryParse(
				match.Groups[1].Value.Replace(',', '.'),
				NumberStyles.AllowDecimalPoint,
				CultureInfo.InvariantCulture,
				out var amount))
		{
			return 0;
		}

		return (long) (amount * sizeUnits.GetValueOrDefault(match.Groups[2].Value.ToUpperInvariant(), 1));
	}

	/// <summary>Первый из <c>cdn</c> и <c>local</c>, ведущий по HTTPS на разрешённый хост.</summary>
	private static Uri? TrustedUrl(JsonNode? urls)
	{
		if (urls is not JsonObject byKind)
		{
			return null;
		}

		foreach (var kind in new[] { "cdn", "local" })
		{
			if (ParseTrustedUrl(PythonText.Of(byKind[kind])) is { } url)
			{
				return url;
			}
		}

		return null;
	}

	/// <summary>HTTPS и хост из списка; используется и при разборе, и при скачивании. Иначе <c>null</c>.</summary>
	public static Uri? ParseTrustedUrl(string candidate) =>
		Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)
		&& parsed.Scheme == Uri.UriSchemeHttps
		&& DownloadHosts.Contains(parsed.Host.ToLowerInvariant())
			? parsed
			: null;
}

/// <summary>Снимок каталога, как его отдаёт <c>/api/catalogue</c>.</summary>
/// <param name="FromNetwork">Источник: сеть или файл кэша.</param>
/// <param name="Stale">Кэш старше суток, а сеть недоступна: каталогом можно пользоваться, но интерфейс предупреждает.</param>
public sealed record CatalogueSnapshot(
	IReadOnlyList<GamePackage> Versions,
	long FetchedAt,
	bool FromNetwork,
	bool Stale,
	ServerPlatform Platform)
{
	public GamePackage? LatestStable => Versions.FirstOrDefault(package => !package.Version.IsUnstable);

	public JsonObject ToJson() => new()
	{
		["versions"] = new JsonArray([.. Versions.Select(package => (JsonNode) package.ToJson())]),
		["fetched_at"] = FetchedAt,
		["source"] = FromNetwork ? "network" : "cache",
		["stale"] = Stale,
		["platform"] = ReleaseManifest.PlatformWire(Platform),
		["latest_stable"] = LatestStable?.Version.Text ?? string.Empty,
	};
}

/// <summary>
/// <c>str()</c> и истинность Python для значений внешнего JSON: число, булево или <c>null</c>
/// на месте строки не ломают разбор манифеста и ответов ModDB.
/// </summary>
public static class PythonText
{
	/// <summary><c>str(value or "")</c>.</summary>
	public static string Of(JsonNode? node)
	{
		if (!Truthy(node))
		{
			return string.Empty;
		}

		if (node is JsonValue value)
		{
			if (value.TryGetValue<string>(out var text)) return text;
			if (value.TryGetValue<bool>(out var flag)) return flag ? "True" : "False";
			if (value.TryGetValue<long>(out var integer)) return integer.ToString(CultureInfo.InvariantCulture);
			if (value.TryGetValue<double>(out var real)) return real.ToString("R", CultureInfo.InvariantCulture);
		}

		return node!.ToJsonString();
	}

	/// <summary><c>bool(value)</c>: пустые строка, список, объект, ноль, <c>false</c> и <c>null</c> ложны.</summary>
	public static bool Truthy(JsonNode? node) => node switch
	{
		null => false,
		JsonArray array => array.Count > 0,
		JsonObject obj => obj.Count > 0,
		JsonValue value when value.TryGetValue<string>(out var text) => text.Length > 0,
		JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
		JsonValue value when value.TryGetValue<double>(out var number) => number != 0,
		_ => true,
	};
}
