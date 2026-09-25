using System.Globalization;
using System.Text.RegularExpressions;
using Panel.SharedKernel;

namespace Panel.Provisioning.Domain;

/// <summary>
/// Одна строка <c>/etc/vs-panel.env</c>, которую можно менять из веб-интерфейса.
/// </summary>
/// <remarks>
/// Шесть ключей, у каждого своё регулярное выражение (ADR-11). <c>PANEL_STATE_DIR</c> и пути к
/// привилегированной стороне здесь нет: менять их из браузера нельзя. Проверка выполняется и в
/// панели, и в демоне, и демон полагается только на свою.
/// </remarks>
public sealed partial record PanelSetting
{
	private static readonly Dictionary<string, Regex> allowed = new(StringComparer.Ordinal)
	{
		["PANEL_BIND"] = BindPattern,
		["PANEL_PORT"] = PortPattern,
		["PANEL_ALLOWED_HOSTS"] = HostsPattern,
		["PANEL_SECURE_COOKIE"] = CookiePattern,
		["PANEL_GAME_VERSION"] = InstanceSpecification.GameVersion,
		// Выбор только среди серверов реестра; каждую команду сервер всё равно проверяет.
		["PANEL_INSTANCE"] = InstancePattern,
	};

	private PanelSetting(string key, string value)
	{
		Key = key;
		Value = value;
	}

	public string Key { get; }

	public string Value { get; }

	public static IReadOnlyCollection<string> Keys => allowed.Keys;

	/// <summary>Неизвестный ключ или неподходящее значение — отказ.</summary>
	public static Result<PanelSetting> Create(string? key, string? value)
	{
		if (key is null || !allowed.TryGetValue(key, out var pattern))
		{
			return Failure.Invalid("This panel setting cannot be changed from the web interface");
		}

		return value is not null && pattern.IsMatch(value)
			? new PanelSetting(key, value)
			: Failure.Invalid($"Invalid value for {key}");
	}

	/// <summary>
	/// Файл окружения с этой настройкой: строка с ключом заменяется, а если её нет, дописывается
	/// в конец. Остальные строки, комментарии и порядок не меняются.
	/// </summary>
	public string Rewrite(IReadOnlyList<string> lines)
	{
		ArgumentNullException.ThrowIfNull(lines);
		var replacement = $"{Key}={Value}";
		var prefix = Key + "=";
		var updated = lines.Select(line => line.StartsWith(prefix, StringComparison.Ordinal) ? replacement : line).ToList();
		if (!lines.Any(line => line.StartsWith(prefix, StringComparison.Ordinal)))
		{
			updated.Add(replacement);
		}

		return string.Join("\n", updated) + "\n";
	}

	[GeneratedRegex(@"\A(?:\d{1,3}(?:\.\d{1,3}){3}|::|::1|0\.0\.0\.0)\z", RegexOptions.CultureInvariant)]
	private static partial Regex BindPattern { get; }

	[GeneratedRegex(@"\A\d{1,5}\z", RegexOptions.CultureInvariant)]
	private static partial Regex PortPattern { get; }

	[GeneratedRegex(@"\A[A-Za-z0-9.,:_-]{1,255}\z", RegexOptions.CultureInvariant)]
	private static partial Regex HostsPattern { get; }

	[GeneratedRegex(@"\A(?:true|false)\z", RegexOptions.CultureInvariant)]
	private static partial Regex CookiePattern { get; }

	[GeneratedRegex(@"\A[a-z0-9][a-z0-9_-]{0,31}\z", RegexOptions.CultureInvariant)]
	private static partial Regex InstancePattern { get; }
}

/// <summary>
/// Шаг мастера «Доступ»: адрес, порт, разрешённые хосты, secure-cookie.
/// </summary>
public sealed record AccessSettings
{
	private AccessSettings(string bind, int port, IReadOnlyList<string> hosts, bool secureCookie)
	{
		Bind = bind;
		Port = port;
		Hosts = hosts;
		SecureCookie = secureCookie;
	}

	public string Bind { get; }

	public int Port { get; }

	public IReadOnlyList<string> Hosts { get; }

	public bool SecureCookie { get; }

	/// <summary>Видна ли панель за пределами машины, как <c>published()</c>.</summary>
	public bool Published => Bind is not ("127.0.0.1" or "::1" or "localhost");

	public static Result<AccessSettings> Create(string? bind, long port, string? allowedHosts, bool secureCookie)
	{
		if (port is < 1 or > 65535)
		{
			return Failure.Invalid("Порт панели должен быть от 1 до 65535");
		}

		var raw = allowedHosts ?? string.Empty;
		var hosts = raw.Split(',').Select(item => item.Trim()).Where(item => item.Length > 0).ToList();
		if (hosts.Count == 0)
		{
			return Failure.Invalid("Укажите хотя бы один разрешённый хост");
		}

		if (ServerSpec.PythonLength(raw) > 255)
		{
			return Failure.Invalid("Слишком длинный список разрешённых хостов");
		}

		return new AccessSettings((bind ?? string.Empty).Trim(), (int) port, hosts, secureCookie);
	}

	/// <summary>Четыре пары для <c>vs-panel.env</c>.</summary>
	public IReadOnlyList<(string Key, string Value)> Values =>
	[
		("PANEL_BIND", Bind),
		("PANEL_PORT", Port.ToString(CultureInfo.InvariantCulture)),
		("PANEL_ALLOWED_HOSTS", string.Join(",", Hosts)),
		("PANEL_SECURE_COOKIE", SecureCookie ? "true" : "false"),
	];

	/// <summary>Адрес, по которому панель откроется после перезапуска.</summary>
	public string Url => $"http://{Bind}:{Port.ToString(CultureInfo.InvariantCulture)}/";
}

/// <summary>Умолчания для нового сервера, не пересекающиеся с реестром: <c>suggest()</c>.</summary>
public static class Suggestion
{
	public static (string Id, int Port) For(IEnumerable<(string Id, int Port)> registered)
	{
		ArgumentNullException.ThrowIfNull(registered);
		var known = registered.ToList();
		var ids = known.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
		var ports = known.Select(item => item.Port).ToHashSet();

		var identifier = "main";
		for (var index = 2; ids.Contains(identifier); index++)
		{
			identifier = $"server{index.ToString(CultureInfo.InvariantCulture)}";
		}

		var port = ServerSpec.DefaultPort;
		while (ports.Contains(port))
		{
			port++;
		}

		return (identifier, port);
	}
}

/// <summary>
/// Сколько места нужно под новый сервер: пакет, распакованное дерево и будущий мир, но не
/// меньше 3 ГиБ.
/// </summary>
public static class DiskRequirement
{
	public const long MinimumFreeBytes = 3L * 1024 * 1024 * 1024;

	public const int PackageMultiplier = 4;

	public static long For(long packageSize) => Math.Max(MinimumFreeBytes, packageSize * PackageMultiplier);

	/// <summary>Препятствие, если место известно и его мало; <c>null</c>, если места хватает или оно неизвестно.</summary>
	public static string? Blocker(long free, long needed)
	{
		const long gib = 1024L * 1024 * 1024;
		return free > 0 && free < needed
			? $"Недостаточно места: свободно {(free / gib).ToString(CultureInfo.InvariantCulture)} ГиБ, нужно около {(needed / gib).ToString(CultureInfo.InvariantCulture)} ГиБ."
			: null;
	}
}
