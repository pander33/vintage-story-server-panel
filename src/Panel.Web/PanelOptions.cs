using System.Globalization;

namespace Panel.Web;

/// <summary>
/// Настройки панели. Имена переменных окружения те же, что в vs-panel.env: установщик пишет
/// шесть ключей, у каждого своё регулярное выражение, и этот список менять нельзя (ADR-11).
/// </summary>
public sealed record PanelOptions
{
	/// <summary>Сокет привилегированного демона.</summary>
	public string AgentSocket { get; init; } = "/run/vs-panel/agent.sock";

	/// <summary>Каталог состояния панели: accounts.json, secret.key, instances/.</summary>
	public string StateDirectory { get; init; } = "/var/lib/vs-panel";

	/// <summary>Файл настроек панели; в нём указано, где лежит реестр серверов.</summary>
	public string PanelConfigFile { get; init; } = "/etc/vs-panel.json";

	/// <summary>Сервер по умолчанию, если запрос не указал другой (PANEL_INSTANCE).</summary>
	public string DefaultInstance { get; init; } = "main";

	public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(12);

	/// <summary>
	/// PANEL_BIND: адрес, на котором слушает панель. Без ключа — только эта машина; поставка
	/// (<c>deploy/*.env</c>) задаёт <c>0.0.0.0</c>, и сменить это можно в мастере или установщике.
	/// </summary>
	public string Bind { get; init; } = "127.0.0.1";

	/// <summary>PANEL_PORT.</summary>
	public int Port { get; init; } = 8080;

	/// <summary>PANEL_ALLOWED_HOSTS: заголовок Host сверяется с этим списком.</summary>
	public IReadOnlyList<string> AllowedHosts { get; init; } = ["localhost", "127.0.0.1"];

	/// <summary>
	/// PANEL_TRUSTED_PROXIES: соединения от этих адресов и сетей несут адрес посетителя в
	/// <c>X-Forwarded-For</c>. Ошибочное значение даёт умолчание — петлю, а не пустой список: так
	/// прокси на той же машине продолжает работать, а чужим доверие не расширяется.
	/// </summary>
	public IReadOnlyList<System.Net.IPNetwork> TrustedProxies { get; init; } =
		Provisioning.Domain.TrustedProxies.Parse(Provisioning.Domain.TrustedProxies.Default).Value;

	/// <summary>
	/// PANEL_SECURE_COOKIE. Выключается для установки без TLS, иначе cookie не дойдёт и панель
	/// будет недоступна по http.
	/// </summary>
	public bool SecureCookie { get; init; } = true;

	public string SecretKeyPath => Path.Combine(StateDirectory, "secret.key");

	public string AccountsPath => Path.Combine(StateDirectory, "accounts.json");

	/// <summary>Устаревший файл пароля; читается один раз, при переносе.</summary>
	public string LegacyPasswordPath => Path.Combine(StateDirectory, "admin.password");

	public string AuditPath => Path.Combine(StateDirectory, "audit.jsonl");

	public int AuditRetentionDays { get; init; } = 90;

	/// <summary>
	/// Каталог, через который крупные файлы передаются демону. Выводится из каталога состояния, а
	/// не настраивается отдельно: демон берёт его из <c>/etc/vs-panel.json</c>, и два независимых
	/// значения могли бы разойтись.
	/// </summary>
	public string StagingDirectory => Path.Combine(StateDirectory, "staging");

	/// <summary>PANEL_MAX_UPLOAD_MB: предел размера загружаемого мода.</summary>
	public int MaxUploadMegabytes { get; init; } = 256;

	/// <summary>PANEL_MAX_WORLD_UPLOAD_MB: предел размера загружаемого мира.</summary>
	public int MaxWorldUploadMegabytes { get; init; } = 4096;

	/// <summary>Интервал снятия телеметрии в секундах; не меньше 2 с, так часто пишет мод.</summary>
	public int TelemetryIntervalSeconds { get; init; } = 2;

	/// <summary>Сколько часов хранится история телеметрии.</summary>
	public int TelemetryHistoryHours { get; init; } = 24;

	/// <summary>Интервал снятия метрик в секундах; не меньше 15 с, потому что замер обходит каталог мира.</summary>
	public int MetricsIntervalSeconds { get; init; } = 60;

	/// <summary>Сколько дней хранится история метрик.</summary>
	public int MetricsHistoryDays { get; init; } = 7;

	/// <summary>Такт планировщика и наблюдателя в секундах (PANEL_AUTOMATION_INTERVAL).</summary>
	public int AutomationIntervalSeconds { get; init; } = 10;

	/// <summary>Сколько дней хранится лента событий сервера.</summary>
	public int EventRetentionDays { get; init; } = 30;

	/// <summary>
	/// PANEL_GAME_VERSION: версия игры, если реестр не знает версии сервера. По ней подбираются
	/// совместимые моды.
	/// </summary>
	public string GameVersion { get; init; } = "1.22.7";

	/// <summary>
	/// PANEL_MODDB_CDN_ADDRESSES: резервные адреса узла CDN ModDB через запятую. Пробуются, если адрес
	/// из DNS не отвечает: из РФ DNS выдавал заблокированный узел, а соседний работал. Значение без
	/// единого адреса (например, <c>none</c>) отключает резерв.
	/// </summary>
	public IReadOnlyList<System.Net.IPAddress> ModDbCdnAddresses { get; init; } = Addresses(DefaultCdnAddresses);

	/// <summary>Узел BunnyCDN, доступный из РФ на 2026-10-01.</summary>
	public const string DefaultCdnAddresses = "79.127.134.133";

	/// <summary>Кэш каталога версий игры, один на панель.</summary>
	public string CatalogueCachePath => Path.Combine(StateDirectory, "catalogue.json");

	/// <summary>Аренда обслуживания, одна на панель, поэтому лежит рядом с <c>accounts.json</c>.</summary>
	public string MaintenanceLeasePath => Path.Combine(StateDirectory, "maintenance-lease.json");

	/// <summary>Состояние мастера установки, одно на панель.</summary>
	public string SetupStatePath => Path.Combine(StateDirectory, "setup.json");

	/// <summary>Одноразовый токен захвата, который печатает установщик.</summary>
	public string SetupTokenPath => Path.Combine(StateDirectory, "setup.token");

	/// <summary>
	/// Текущее создание сервера, на уровне панели: сервера, в каталоге которого его можно было бы
	/// хранить, ещё нет.
	/// </summary>
	public string ProvisionStatePath => Path.Combine(StateDirectory, "provision.json");

	public string ProvisionHistoryPath => Path.Combine(StateDirectory, "provision-runs.jsonl");

	/// <summary>Настройки уведомлений, общие для всей панели.</summary>
	public string NotificationsPath => Path.Combine(StateDirectory, "notifications.json");

	/// <summary>Каталог состояния одного сервера: <c>instances/&lt;id&gt;/</c>.</summary>
	public string InstanceDirectory(Panel.SharedKernel.InstanceId instance) =>
		Path.Combine(StateDirectory, "instances", instance.Value);

	/// <summary>Каталог панели на Windows (<c>C:\ProgramData\vs-panel</c>).</summary>
	private static string WindowsRoot =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "vs-panel");

	/// <summary>
	/// Настройки из окружения процесса, а на Windows ещё и из <c>vs-panel.env</c>.
	/// </summary>
	/// <remarks>
	/// На Linux файл окружения читает systemd (<c>EnvironmentFile=</c>), и панель получает готовые
	/// переменные. Служба Windows так не умеет, поэтому файл читает сама панель, иначе ключи,
	/// которые пишет <c>panel-env-set</c> из мастера, ни на что бы не влияли. Переменная, заданная
	/// процессу явно, важнее строки файла. Умолчания путей на Windows берутся из раскладки Windows.
	/// </remarks>
	public static PanelOptions FromEnvironment()
	{
		var windows = OperatingSystem.IsWindows();
		var file = windows
			? ReadEnvironmentFile(Environment.GetEnvironmentVariable("PANEL_ENV_FILE") is { Length: > 0 } declared
				? declared
				: Path.Combine(WindowsRoot, "vs-panel.env"))
			: new Dictionary<string, string>(StringComparer.Ordinal);

		string Text(string key, string fallback) =>
			Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value
			: file.TryGetValue(key, out var written) && written.Length > 0 ? written
			: fallback;

		int Number(string key, int fallback) =>
			int.TryParse(Text(key, string.Empty), CultureInfo.InvariantCulture, out var value) ? value : fallback;

		return Build(windows, Text, Number);
	}

	/// <summary>
	/// Построчный <c>KEY=VALUE</c>, как его пишут установщик и демон. Если файла нет, набор пуст, и
	/// панель запустится с умолчаниями.
	/// </summary>
	public static Dictionary<string, string> ReadEnvironmentFile(string path)
	{
		var values = new Dictionary<string, string>(StringComparer.Ordinal);
		try
		{
			foreach (var raw in File.ReadLines(path))
			{
				var line = raw.Trim();
				var separator = line.IndexOf('=', StringComparison.Ordinal);
				if (line.Length == 0 || line.StartsWith('#') || separator <= 0)
				{
					continue;
				}

				values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Нечитаемый файл даёт те же умолчания, что и отсутствующий.
		}

		return values;
	}

	/// <summary>Адреса через запятую; не адрес пропускается.</summary>
	public static IReadOnlyList<System.Net.IPAddress> Addresses(string value) =>
	[
		.. value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
			.Select(item => System.Net.IPAddress.TryParse(item, out var address) ? address : null)
			.OfType<System.Net.IPAddress>(),
	];

	private static PanelOptions Build(bool windows, Func<string, string, string> text, Func<string, int, int> number) => new()
	{
		AgentSocket = text("PANEL_AGENT_SOCKET", windows ? @"\\.\pipe\vs-panel-agent" : "/run/vs-panel/agent.sock"),
		StateDirectory = text("PANEL_STATE_DIR", windows ? Path.Combine(WindowsRoot, "state") : "/var/lib/vs-panel"),
		PanelConfigFile = text("PANEL_CONFIG_FILE", windows ? Path.Combine(WindowsRoot, "vs-panel.json") : "/etc/vs-panel.json"),
		DefaultInstance = text("PANEL_INSTANCE", "main"),
		SessionLifetime = TimeSpan.FromHours(number("PANEL_SESSION_HOURS", 12)),
		Bind = text("PANEL_BIND", "127.0.0.1"),
		Port = number("PANEL_PORT", 8080),
		AllowedHosts =
		[
			.. text("PANEL_ALLOWED_HOSTS", "localhost,127.0.0.1")
				.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
		],

		TrustedProxies = Provisioning.Domain.TrustedProxies.Parse(text(Provisioning.Domain.TrustedProxies.Key, Provisioning.Domain.TrustedProxies.Default)) is { IsSuccess: true } proxies
			? proxies.Value
			: Provisioning.Domain.TrustedProxies.Parse(Provisioning.Domain.TrustedProxies.Default).Value,

		// По умолчанию включено: незащищённый режим нужно включать явно.
		SecureCookie = text("PANEL_SECURE_COOKIE", "1") is not ("0" or "false" or "no"),
		AuditRetentionDays = number("PANEL_AUDIT_RETENTION_DAYS", 90),
		MaxUploadMegabytes = number("PANEL_MAX_UPLOAD_MB", 256),
		MaxWorldUploadMegabytes = number("PANEL_MAX_WORLD_UPLOAD_MB", 4096),
		GameVersion = text("PANEL_GAME_VERSION", "1.22.7"),
		ModDbCdnAddresses = Addresses(text(Catalogues.Infrastructure.Reachability.CdnAddressesKey, DefaultCdnAddresses)),

		// Нижние границы интервалов.
		TelemetryIntervalSeconds = Math.Max(2, number("PANEL_TELEMETRY_INTERVAL", 2)),
		TelemetryHistoryHours = Math.Max(1, number("PANEL_TELEMETRY_HISTORY_HOURS", 24)),
		MetricsIntervalSeconds = Math.Max(15, number("PANEL_METRICS_INTERVAL", 60)),
		MetricsHistoryDays = Math.Max(1, number("PANEL_METRICS_HISTORY_DAYS", 7)),
		EventRetentionDays = Math.Max(1, number("PANEL_EVENT_RETENTION_DAYS", 30)),
		AutomationIntervalSeconds = Math.Max(5, number("PANEL_AUTOMATION_INTERVAL", 10)),
	};
}
