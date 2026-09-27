using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Panel.SharedKernel;

namespace Panel.Provisioning.Domain;

/// <summary>
/// Все пути сервера, выведенные из его идентификатора.
/// </summary>
/// <remarks>
/// <para>
/// Панель присылает только идентификатор. Корень, данные, моды, миры, журналы, копии, имя
/// юнита и FIFO демон выводит сам из <c>instances_root</c> (INV-43), и где окажется сервер,
/// решает только этот тип.
/// </para>
/// <para>
/// Раскладок две. На Linux — юнит systemd и FIFO консоли в каталоге выполнения. На Windows
/// юнита и FIFO нет: сервер — дочерний процесс службы-супервизора, которая держит его stdin, а
/// «юнит» — только имя (<c>vs-&lt;id&gt;</c>), под которым сервер знают супервизор и интерфейс.
/// Транспорт консоли на Windows — <c>supervised</c>. Разделитель путей берётся по раскладке, а
/// не по текущей машине, поэтому раскладку можно проверить тестом на любой платформе.
/// </para>
/// </remarks>
public sealed partial record InstanceLayout
{
	private InstanceLayout()
	{
	}

	public InstanceId Id { get; private init; }
	public PlatformLayout Layout { get; private init; }
	public string InstancesRoot { get; private init; } = string.Empty;
	public string Root { get; private init; } = string.Empty;
	public string ProgramDirectory { get; private init; } = string.Empty;
	public string DataDirectory { get; private init; } = string.Empty;
	public string ModsDirectory { get; private init; } = string.Empty;
	public string WorldDirectory { get; private init; } = string.Empty;
	public string LogsDirectory { get; private init; } = string.Empty;
	public string ModConfigDirectory { get; private init; } = string.Empty;
	public string ConfigFile { get; private init; } = string.Empty;
	public string BackupDirectory { get; private init; } = string.Empty;
	public string TelemetryFile { get; private init; } = string.Empty;
	public string GameEventsFile { get; private init; } = string.Empty;
	public string Unit { get; private init; } = string.Empty;
	public string RuntimeDirectory { get; private init; } = string.Empty;
	public string ConsoleFifo { get; private init; } = string.Empty;

	/// <summary>
	/// Каталоги, которые создаются пустыми до распаковки программы.
	/// </summary>
	public IReadOnlyList<string> SkeletonDirectories =>
	[
		DataDirectory, ModsDirectory, WorldDirectory, LogsDirectory, ModConfigDirectory, BackupDirectory,
		Path.Combine(DataDirectory, "VsPanel"),
	];

	/// <summary>Транспорт консоли, который получает созданный сервер.</summary>
	public string CommandTransport => Layout == PlatformLayout.Windows ? "supervised" : "fifo";

	/// <summary>
	/// Раскладка сервера. Корень и каталог FIFO должны быть абсолютными путями из безопасных
	/// символов: они попадают в <c>ExecStart</c> юнита, где пробел разделяет аргументы, а перевод
	/// строки начинает новую директиву. На Windows юнита нет, но правило то же: путь без пробелов
	/// не нужно правильно экранировать.
	/// </summary>
	public static Result<InstanceLayout> Create(
		string instancesRoot,
		string runDirectory,
		InstanceId id,
		PlatformLayout layout = PlatformLayout.Linux)
	{
		if (id.IsEmpty || id.IsRegistry)
		{
			return Failure.Invalid("Недопустимый идентификатор сервера");
		}

		var windows = layout == PlatformLayout.Windows;
		var root = windows ? NormalizeWindows(instancesRoot) : Normalize(instancesRoot);
		var run = windows ? NormalizeWindows(runDirectory) : Normalize(runDirectory);
		if (root is null || run is null)
		{
			return Failure.Unavailable("Каталог серверов или каталог выполнения задан небезопасным путём");
		}

		var separator = windows ? '\\' : '/';
		string Join(params string[] parts) => string.Join(separator, parts);

		var instance = Join(root, id.Value);
		var data = Join(instance, "data");
		return new InstanceLayout
		{
			Id = id,
			Layout = layout,
			InstancesRoot = root,
			Root = instance,
			ProgramDirectory = Join(instance, "server"),
			DataDirectory = data,
			ModsDirectory = Join(data, "Mods"),
			WorldDirectory = Join(data, "Saves"),
			LogsDirectory = Join(data, "Logs"),
			ModConfigDirectory = Join(data, "ModConfig"),
			ConfigFile = Join(data, "serverconfig.json"),
			BackupDirectory = Join(instance, "backups"),
			TelemetryFile = Join(data, "VsPanel", "telemetry.json"),
			GameEventsFile = Join(data, "VsPanel", "events.jsonl"),
			Unit = windows ? $"vs-{id.Value}" : $"vs-{id.Value}.service",
			RuntimeDirectory = windows ? string.Empty : $"vs-{id.Value}",
			ConsoleFifo = windows ? string.Empty : $"{run}/vs-{id.Value}/console.fifo",
		};
	}

	/// <summary>Файл мира по умолчанию, который получает новый сервер.</summary>
	public string DefaultWorldFile =>
		WorldDirectory + (Layout == PlatformLayout.Windows ? "\\" : "/") + "default.vcdbs";

	/// <summary>
	/// Запись реестра для созданного сервера.
	/// </summary>
	public JsonObject RegistryEntry(ServerSpec spec)
	{
		ArgumentNullException.ThrowIfNull(spec);
		var entry = new JsonObject
		{
			["name"] = spec.Name,
			["unit"] = Unit,
			["port"] = spec.Port,
			["game_version"] = spec.Version,
			["data_dir"] = DataDirectory,
			["mods_dir"] = ModsDirectory,
			["backup_dir"] = BackupDirectory,
			["config_file"] = ConfigFile,
			["world_dir"] = WorldDirectory,
			["logs_dir"] = LogsDirectory,
			["telemetry_file"] = TelemetryFile,
			["game_events_file"] = GameEventsFile,
			["mod_config_dir"] = ModConfigDirectory,
			["program_dir"] = ProgramDirectory,
			["command_transport"] = CommandTransport,
		};

		if (ConsoleFifo.Length > 0)
		{
			entry["console_fifo"] = ConsoleFifo;
		}

		return entry;
	}

	[GeneratedRegex(@"\A(/[A-Za-z0-9._-]+)+\z", RegexOptions.CultureInvariant)]
	private static partial Regex SafePath { get; }

	[GeneratedRegex(@"\A[A-Za-z]:(\\[A-Za-z0-9._-]+)+\z", RegexOptions.CultureInvariant)]
	private static partial Regex SafeWindowsPath { get; }

	/// <summary>Путь в нормальной форме, как его видит <see cref="Create"/>; небезопасный путь даёт <see langword="null"/>.</summary>
	internal static string? NormalizeRoot(string raw, PlatformLayout layout) =>
		layout == PlatformLayout.Windows ? NormalizeWindows(raw) : Normalize(raw);

	private static string? Normalize(string raw)
	{
		var value = (raw ?? string.Empty).TrimEnd('/');
		return SafePath.IsMatch(value) && !value.Split('/').Any(part => part is "." or "..") ? value : null;
	}

	/// <summary>
	/// <c>C:\ProgramData\vs-panel\servers</c>: буква диска и сегменты из безопасных символов.
	/// Прямая косая черта заменяется обратной: Windows принимает обе, а пути реестра и выведенные
	/// пути сравниваются как строки.
	/// </summary>
	private static string? NormalizeWindows(string raw)
	{
		var value = (raw ?? string.Empty).Replace('/', '\\').TrimEnd('\\');
		return SafeWindowsPath.IsMatch(value) && !value.Split('\\').Any(part => part is "." or "..") ? value : null;
	}
}

/// <summary>
/// Юнит systemd игрового сервера.
/// </summary>
/// <remarks>
/// Юнит пишется только из этого шаблона, и каждая подстановка выведена демоном, поэтому
/// подставить свой <c>ExecStart</c> нельзя (INV-43). Пути dotnet и обёртки консоли проверяются
/// тем же правилом, что и корень: без пробелов и переводов строк.
/// </remarks>
public sealed partial record GameUnit
{
	/// <summary>Пользователь, от которого работает игра.</summary>
	public const string GameUser = "vsgame";

	private GameUnit(string text) => Text = text;

	public string Text { get; }

	public static Result<GameUnit> Create(InstanceLayout layout, string dotnet, string wrapper)
	{
		ArgumentNullException.ThrowIfNull(layout);
		if (!Executable.IsMatch(dotnet ?? string.Empty) || !Executable.IsMatch(wrapper ?? string.Empty))
		{
			return Failure.Unavailable("Путь к dotnet или к обёртке консоли небезопасен для юнита");
		}

		// Переводы строк LF: юнит читает systemd, и CR в конце ExecStart стал бы частью последнего
		// аргумента.
		var text =
			"[Unit]\n" +
			$"Description=Vintage Story Server ({layout.Id.Value})\n" +
			"After=network.target\n" +
			"\n" +
			"[Service]\n" +
			"Type=simple\n" +
			$"User={GameUser}\n" +
			$"Group={GameUser}\n" +
			$"RuntimeDirectory={layout.RuntimeDirectory}\n" +
			"RuntimeDirectoryMode=0750\n" +
			$"WorkingDirectory={layout.ProgramDirectory}\n" +
			$"ExecStart={wrapper} {layout.ConsoleFifo} {dotnet} {layout.ProgramDirectory}/VintagestoryServer.dll --dataPath {layout.DataDirectory} --append --archiveLogFileCount 14 --archiveLogFileMaxSizeMb 1024\n" +
			"Restart=on-failure\n" +
			"RestartSec=10s\n" +
			"TimeoutStopSec=300s\n" +
			"UMask=0027\n" +
			"\n" +
			"[Install]\n" +
			"WantedBy=multi-user.target\n";
		return new GameUnit(text);
	}

	[GeneratedRegex(@"\A(/[A-Za-z0-9._+-]+)+\z", RegexOptions.CultureInvariant)]
	private static partial Regex Executable { get; }
}

/// <summary>
/// Значения панели поверх конфигурации, которую сгенерировала игра.
/// </summary>
/// <remarks>
/// Роли по умолчанию есть только в сгенерированном файле: минимальный файл, написанный вручную,
/// игра не принимает на этапе Configuration («default group code suplayer but no such group
/// exists»). Поэтому значения только накладываются, а <c>WorldConfig</c> объединяется, а не
/// заменяется, и шаблон мира игры сохраняется.
/// </remarks>
public static class InitialConfig
{
	public static JsonObject Apply(JsonObject generated, ServerSpec spec, InstanceLayout layout)
	{
		ArgumentNullException.ThrowIfNull(generated);
		ArgumentNullException.ThrowIfNull(spec);
		ArgumentNullException.ThrowIfNull(layout);

		var value = (JsonObject) generated.DeepClone();
		value["ServerName"] = spec.ServerName;
		value["ServerDescription"] = spec.Description;
		value["WelcomeMessage"] = "Добро пожаловать, {0}!";
		value["Port"] = spec.Port;
		value["MaxClients"] = spec.MaxPlayers;
		value["Password"] = string.Empty;
		value["AdvertiseServer"] = false;
		value["AllowPvP"] = spec.Pvp;
		value["ServerLanguage"] = spec.Language;

		// 0 — по умолчанию, 1 — выключен, 2 — включён; та же кодировка, что в редакторе.
		value["WhitelistMode"] = spec.Whitelist ? 2 : 1;

		var world = value["WorldConfig"] is JsonObject template ? (JsonObject) template.DeepClone() : new JsonObject();
		world["SaveFileLocation"] = layout.DefaultWorldFile;
		world["WorldName"] = spec.WorldName;
		world["Seed"] = spec.Seed.Length > 0 ? spec.Seed : null;
		world["PlayStyle"] = spec.Playstyle;
		world["PlayStyleLangCode"] = "preset-" + spec.Playstyle;
		value["WorldConfig"] = world;
		value["ModPaths"] ??= new JsonArray("Mods", layout.ModsDirectory);
		return ServerConfigPaths.FromLayout(layout).Rebase(value).Value;
	}
}
