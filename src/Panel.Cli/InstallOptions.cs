using System.Globalization;
using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Cli;

/// <summary>
/// Настройки установки из переменных окружения и аргументов.
/// </summary>
internal sealed record InstallOptions
{
	/// <summary>Каталог сборки; для <c>repair</c> — активная версия.</summary>
	public string From { get; init; } = string.Empty;

	/// <summary>Переустановка без копирования версии, без пакетов и без создания сервера.</summary>
	public bool Repair { get; init; }

	/// <summary><c>VS_PANEL_CREATE_SERVER</c>: 1 — создать первый сервер, 0 — оставить это мастеру.</summary>
	public bool CreateServer { get; init; } = true;

	/// <summary><c>VS_PANEL_GAME_VERSION</c>: версия первого сервера; пусто — последняя стабильная.</summary>
	public string GameVersion { get; init; } = string.Empty;

	/// <summary><c>VS_PANEL_GAME_PORT</c>: порт первого сервера; 0 — умолчание мастера.</summary>
	public int GamePort { get; init; }

	/// <summary><c>VS_PANEL_INSTALL_WAIT_SECONDS</c>: сколько ждать перезапуска панели, не меньше 10.</summary>
	public int WaitSeconds { get; init; } = 120;

	/// <summary><c>VS_PANEL_DOTNET_CHANNEL</c>: среда .NET для игры; панели она не нужна.</summary>
	public string DotnetChannel { get; init; } = "10.0";

	public bool SkipDotnet { get; init; }

	/// <summary><c>VS_PANEL_SKIP_PACKAGES</c>: не ставить системные пакеты через apt.</summary>
	public bool SkipPackages { get; init; }

	/// <summary><c>VS_TELEMETRY_MOD_DIR</c>: куда класть мод телеметрии для сервера в /srv/vintagestory.</summary>
	public string TelemetryModDirectory { get; init; } = string.Empty;

	/// <summary>
	/// <c>VS_PANEL_SERVERS_DIR</c> или <c>--servers-dir</c>: каталог, в котором создаются игровые
	/// серверы. Если пусто, берётся прежний каталог или умолчание раскладки; при первой установке
	/// в терминале установщик спросит сам.
	/// </summary>
	public string ServersDirectory { get; init; } = string.Empty;

	/// <summary>Windows: открыть мастер установки в браузере; с <c>--no-browser</c> только напечатать ссылку.</summary>
	public bool OpenBrowser { get; init; } = true;

	/// <summary>
	/// Строки <c>vs-panel.env</c>, которые оператор задал при установке: <c>--access</c>,
	/// <c>--bind</c>, <c>--port</c>, <c>--allowed-hosts</c>, <c>--secure-cookie</c>,
	/// <c>--firewall</c> или переменные <c>VS_PANEL_*</c> с теми же именами. Записываются и при
	/// первой установке, и при обновлении или восстановлении; не заданные не меняются.
	/// </summary>
	public IReadOnlyDictionary<string, string> PanelEnvironment { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

	/// <summary>Параметры доступа к панели: имя параметра, переменная окружения, ключ <c>vs-panel.env</c>.</summary>
	private static readonly (string Argument, string Variable, string Key)[] accessParameters =
	[
		("--bind", "VS_PANEL_BIND", "PANEL_BIND"),
		("--port", "VS_PANEL_PORT", "PANEL_PORT"),
		("--allowed-hosts", "VS_PANEL_ALLOWED_HOSTS", "PANEL_ALLOWED_HOSTS"),
		("--secure-cookie", "VS_PANEL_SECURE_COOKIE", "PANEL_SECURE_COOKIE"),
		("--firewall", "VS_PANEL_FIREWALL", PanelListener.FirewallKey),
	];

	/// <summary>
	/// <c>--access</c>: готовый набор. <c>localhost</c> — только эта машина; <c>lan</c> — все
	/// интерфейсы, порт открыт локальной сети; <c>internet</c> — все интерфейсы, порт открыт всем.
	/// Сетевые наборы разрешают любое имя в Host: по адресу в сети панель ищут чаще, чем по имени,
	/// а прежний список <c>localhost,127.0.0.1</c> ответил бы на такой запрос 400.
	/// Отдельные параметры уточняют набор, в каком бы порядке ни стояли.
	/// </summary>
	private static Result<Dictionary<string, string>> Preset(string access) => access switch
	{
		"localhost" => new Dictionary<string, string>(StringComparer.Ordinal) { ["PANEL_BIND"] = "127.0.0.1" },
		"lan" => new Dictionary<string, string>(StringComparer.Ordinal) { ["PANEL_BIND"] = "0.0.0.0", ["PANEL_ALLOWED_HOSTS"] = "*", [PanelListener.FirewallKey] = "lan" },
		"internet" => new Dictionary<string, string>(StringComparer.Ordinal) { ["PANEL_BIND"] = "0.0.0.0", ["PANEL_ALLOWED_HOSTS"] = "*", [PanelListener.FirewallKey] = "internet" },
		_ => Failure.Invalid($"--access: expected localhost, lan or internet; got '{access}'."),
	};

	/// <summary>Значение строки <c>vs-panel.env</c>: те же правила, что у мастера установки и демона.</summary>
	private static Result<string> Check(string key, string value)
	{
		if (key == PanelListener.FirewallKey)
		{
			return FirewallScopeWire.FromWire(value) is null
				? Failure.Invalid($"--firewall: expected lan, internet or off; got '{value}'.")
				: value;
		}

		if (key == "PANEL_PORT" && !(int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535))
		{
			return Failure.Invalid("Panel port must be between 1 and 65535.");
		}

		return !PanelSetting.Create(key, value).IsSuccess
			? Failure.Invalid($"Invalid value for {key}: '{value}'.")
			: value;
	}

	public static Result<InstallOptions> Parse(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, InstallPaths paths)
	{
		ArgumentNullException.ThrowIfNull(paths);
		return Parse(arguments, environment, paths.LegacyModsDirectory, windows: false);
	}

	/// <summary>
	/// Настройки установки на Windows: без системных пакетов и сервера в <c>/srv/vintagestory</c>,
	/// но с браузером.
	/// </summary>
	public static Result<InstallOptions> ParseWindows(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment) =>
		Parse(arguments, environment, string.Empty, windows: true);

	private static Result<InstallOptions> Parse(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, string legacyMods, bool windows)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		ArgumentNullException.ThrowIfNull(environment);

		string Env(string key) => environment.TryGetValue(key, out var value) ? value.Trim() : string.Empty;

		var options = new InstallOptions
		{
			CreateServer = Env("VS_PANEL_CREATE_SERVER") is not "0",
			GameVersion = Env("VS_PANEL_GAME_VERSION"),
			DotnetChannel = Env("VS_PANEL_DOTNET_CHANNEL") is { Length: > 0 } channel ? channel : "10.0",
			SkipDotnet = Env("VS_PANEL_SKIP_DOTNET") == "1",
			SkipPackages = Env("VS_PANEL_SKIP_PACKAGES") == "1",
			ServersDirectory = Env("VS_PANEL_SERVERS_DIR"),
			TelemetryModDirectory = Env("VS_TELEMETRY_MOD_DIR") is { Length: > 0 } mods ? mods : legacyMods,
		};

		if (Env("VS_PANEL_INSTALL_WAIT_SECONDS") is { Length: > 0 } wait)
		{
			if (!int.TryParse(wait, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds < 10)
			{
				return Failure.Invalid("VS_PANEL_INSTALL_WAIT_SECONDS must be an integer of at least 10.");
			}

			options = options with { WaitSeconds = seconds };
		}

		if (Env("VS_PANEL_GAME_PORT") is { Length: > 0 } port)
		{
			if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1024 or > 65535)
			{
				return Failure.Invalid("VS_PANEL_GAME_PORT must be a number between 1024 and 65535.");
			}

			options = options with { GamePort = number };
		}

		var access = Env("VS_PANEL_ACCESS");
		var explicitValues = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var (_, variable, key) in accessParameters)
		{
			if (Env(variable) is { Length: > 0 } value)
			{
				explicitValues[key] = value;
			}
		}

		for (var index = 0; index < arguments.Count; index++)
		{
			var parameter = Array.Find(accessParameters, item => item.Argument == arguments[index]);
			if (parameter.Argument is not null)
			{
				if (index + 1 >= arguments.Count || arguments[index + 1].Trim().Length == 0)
				{
					return Failure.Invalid($"{parameter.Argument}: value is missing.");
				}

				explicitValues[parameter.Key] = arguments[++index].Trim();
				continue;
			}

			switch (arguments[index])
			{
				case "--access" when index + 1 < arguments.Count:
					access = arguments[++index].Trim();
					break;
				case "--from" when index + 1 < arguments.Count:
					options = options with { From = arguments[++index] };
					break;
				case "--servers-dir" when index + 1 < arguments.Count && arguments[index + 1].Trim().Length > 0:
					options = options with { ServersDirectory = arguments[++index].Trim() };
					break;
				case "--no-server":
					options = options with { CreateServer = false };
					break;
				case "--skip-dotnet":
					options = options with { SkipDotnet = true };
					break;
				case "--skip-packages" when !windows:
					options = options with { SkipPackages = true };
					break;
				case "--no-browser" when windows:
					options = options with { OpenBrowser = false };
					break;
				default:
					return Failure.Invalid($"Unknown option: {arguments[index]}");
			}
		}

		var environmentValues = new Dictionary<string, string>(StringComparer.Ordinal);
		if (access.Length > 0)
		{
			var preset = Preset(access);
			if (!preset.IsSuccess)
			{
				return preset.Failure!;
			}

			environmentValues = preset.Value;
		}

		foreach (var (key, value) in explicitValues)
		{
			environmentValues[key] = value;
		}

		foreach (var (key, value) in environmentValues)
		{
			var checkedValue = Check(key, value);
			if (!checkedValue.IsSuccess)
			{
				return checkedValue.Failure!;
			}
		}

		return options with { PanelEnvironment = environmentValues };
	}
}
