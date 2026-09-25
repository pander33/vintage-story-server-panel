namespace Panel.Cli;

/// <summary>
/// Расположение файлов после установки на Windows. В тестах корни подменяются.
/// </summary>
/// <remarks>
/// <para>
/// Программа лежит в <c>Program Files</c>, по каталогу на версию; активная — символическая
/// ссылка <c>current</c>, как <c>/opt/vs-panel</c> на Linux. Службы запускают <c>current\…</c>,
/// поэтому смена версии меняет ссылку, а не службы. В отличие от <c>ProgramData</c>, в
/// каталог программ обычный пользователь не может добавить свои файлы рядом с бинарниками.
/// </para>
/// <para>
/// Данные лежат в <c>%ProgramData%\vs-panel</c>; этот же каталог по умолчанию используют демон
/// и панель (<c>PanelConfiguration</c>, <c>PanelOptions</c>).
/// </para>
/// </remarks>
internal sealed record WindowsPaths(string ProgramRoot, string DataRoot)
{
	public static WindowsPaths System { get; } = new(
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vs-panel"),
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "vs-panel"));

	/// <summary>Служба веб-процесса; это имя ожидают <c>UseWindowsService</c> и демон.</summary>
	public const string PanelService = "vs-panel";

	/// <summary>Служба демона; имя из <c>AgentWindowsService</c>.</summary>
	public const string AgentService = "vs-panel-agent";

	/// <summary>Именованный канал демона, умолчание <c>PANEL_AGENT_SOCKET</c> на Windows.</summary>
	public const string AgentPipe = "vs-panel-agent";

	public string Releases => Path.Combine(ProgramRoot, "releases");

	/// <summary>Ссылка на активную версию; на неё указывают службы и PATH.</summary>
	public string Current => Path.Combine(ProgramRoot, "current");

	public string ReleaseLog => Path.Combine(ProgramRoot, "releases.log");

	public string PanelConfig => Path.Combine(DataRoot, "vs-panel.json");

	public string PanelEnv => Path.Combine(DataRoot, "vs-panel.env");

	public string Registry => Path.Combine(DataRoot, "instances.json");

	public string StateDirectory => Path.Combine(DataRoot, "state");

	public string ServersDirectory => Path.Combine(DataRoot, "servers");

	/// <summary>Файлы супервизора: <c>&lt;id&gt;.json</c> каждого сервера и его журнал консоли.</summary>
	public string RunDirectory => Path.Combine(DataRoot, "run");

	public string TelemetryDirectory => Path.Combine(DataRoot, "telemetry");

	/// <summary>Файлы прежней версии панели на Windows. Установщик их не трогает.</summary>
	public IReadOnlyList<string> LegacyProgram =>
		[Path.Combine(DataRoot, "app"), Path.Combine(DataRoot, "helper"), Path.Combine(DataRoot, "helper.token"), Path.Combine(DataRoot, "vs-panel-run.ps1")];

	/// <summary>Задания планировщика прежней версии панели: они заняли бы порт и канал раньше служб.</summary>
	public static IReadOnlyList<string> LegacyTasks { get; } = ["VintageStoryPanel", "VintageStoryPanelService"];

	public string Release(string version) => Path.Combine(Releases, version);

	public string CurrentBinary(string name) => Path.Combine(Current, name + ".exe");

	/// <summary>
	/// Учётная запись обеих служб — LocalSystem: без пароля, без права «Вход в качестве службы»
	/// и без изменений учётной записи пользователя (§69).
	/// </summary>
	public const string ServiceAccount = "LocalSystem";

	/// <summary>Работает ли служба от LocalSystem; диспетчер записывает её под одним из двух имён.</summary>
	public static bool IsServiceAccount(string account) =>
		string.Equals(account, ServiceAccount, StringComparison.OrdinalIgnoreCase)
		|| string.Equals(account, @"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Командная строка службы демона с явными каналом и настройками. <c>--allow-sid</c> не
	/// нужен: панель работает от той же LocalSystem, что и демон, а установщику в командной
	/// строке администратора канал открыт правилом для администраторов.
	/// </summary>
	public string AgentCommandLine =>
		$"{Quote(CurrentBinary("vs-panel-agent"))} serve --pipe {AgentPipe} --panel-config {Quote(PanelConfig)}";

	public string PanelCommandLine => Quote(CurrentBinary("vs-panel-web"));

	/// <summary>
	/// Путь в командной строке службы берётся в кавычки: без них путь с пробелом в
	/// <c>Program Files</c> даёт уязвимость «незакавыченного пути службы».
	/// </summary>
	private static string Quote(string value) => $"\"{value}\"";
}
