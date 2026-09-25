using System.Globalization;
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
				return Failure.Invalid("VS_PANEL_INSTALL_WAIT_SECONDS должен быть целым числом не меньше 10.");
			}

			options = options with { WaitSeconds = seconds };
		}

		if (Env("VS_PANEL_GAME_PORT") is { Length: > 0 } port)
		{
			if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1024 or > 65535)
			{
				return Failure.Invalid("VS_PANEL_GAME_PORT должен быть числом от 1024 до 65535.");
			}

			options = options with { GamePort = number };
		}

		for (var index = 0; index < arguments.Count; index++)
		{
			switch (arguments[index])
			{
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
					return Failure.Invalid($"Неизвестный параметр: {arguments[index]}");
			}
		}

		return options;
	}
}
