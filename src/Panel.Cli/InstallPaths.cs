namespace Panel.Cli;

/// <summary>
/// Расположение файлов после установки на Linux. В тестах корень подменяется, на машине это «/».
/// </summary>
/// <remarks>
/// Раскладка программы описана в §3 заметки «Установщик и поставка .NET-панели»: каждая версия
/// в своём каталоге, активная — символическая ссылка <c>current</c>. Юниты ссылаются на
/// <c>current</c>, поэтому смена версии и откат сводятся к замене ссылки. Пути к файлам
/// состояния и настроек заморожены (ADR-04, ADR-11).
/// </remarks>
internal sealed record InstallPaths(string Root)
{
	public static InstallPaths System { get; } = new("/");

	public const string PanelUser = "vs-panel";

	public const string PanelUnitName = "vs-panel.service";

	public const string AgentUnitName = "vs-panel-agent.service";

	public string ProgramRoot => At("opt/vs-panel");

	/// <summary>Установленные версии: <c>releases/&lt;версия&gt;/</c>.</summary>
	public string Releases => Path.Combine(ProgramRoot, "releases");

	/// <summary>Ссылка на активную версию; на неё указывают юниты.</summary>
	public string Current => Path.Combine(ProgramRoot, "current");

	/// <summary>Порядок активаций: по нему выбираются версия для отката и версии к удалению.</summary>
	public string ReleaseLog => Path.Combine(ProgramRoot, "releases.log");

	public string CommandLink => At("usr/local/bin/vs-panel");

	/// <summary>Обёртка консоли для юнитов серверов с транспортом fifo.</summary>
	public string ConsoleWrapper => At("usr/local/libexec/vs-console-wrapper");

	public string SharedData => At("usr/local/share/vs-panel");

	/// <summary>Копия мода телеметрии, из которой демон кладёт мод в каждый новый сервер.</summary>
	public string TelemetryDirectory => Path.Combine(SharedData, "telemetry");

	public string PanelConfig => At("etc/vs-panel.json");

	public string PanelEnv => At("etc/vs-panel.env");

	public string RegistryDirectory => At("etc/vs-panel");

	public string Registry => Path.Combine(RegistryDirectory, "instances.json");

	public string StateDirectory => At("var/lib/vs-panel");

	public string UnitDirectory => At("etc/systemd/system");

	public string PanelUnit => Path.Combine(UnitDirectory, PanelUnitName);

	public string AgentUnit => Path.Combine(UnitDirectory, AgentUnitName);

	public string AgentSocket => At("run/vs-panel/agent.sock");

	/// <summary>Сервер, поставленный вручную до панели; установщик берёт его под управление как main.</summary>
	public string LegacyServerConfig => At("srv/vintagestory/data/serverconfig.json");

	public string LegacyModsDirectory => At("srv/vintagestory/data/Mods");

	/// <summary>sudo-правило прежней версии панели. Панели sudo не нужен, правило удаляется (INV-51).</summary>
	public string LegacySudoers => At("etc/sudoers.d/vs-panel");

	public string LegacyHelper => At("usr/local/libexec/vs-panel-helper");

	public string LegacyClaim => At("usr/local/bin/vs-panel-claim");

	/// <summary>Файлы прежней версии панели. Установщик их не трогает, <c>doctor</c> о них напоминает.</summary>
	public IReadOnlyList<string> LegacyProgram =>
		[Path.Combine(ProgramRoot, ".venv"), Path.Combine(ProgramRoot, "app"), Path.Combine(ProgramRoot, "pyproject.toml")];

	public string Release(string version) => Path.Combine(Releases, version);

	public string CurrentBinary(string name) => Path.Combine(Current, name);

	private string At(string relative) => Path.Combine(Root, relative);
}
