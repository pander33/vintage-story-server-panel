using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Panel.Provisioning.Domain;

/// <summary>
/// Кому брандмауэр открывает порт панели: ключ <c>PANEL_FIREWALL</c> в <c>vs-panel.env</c>.
/// </summary>
/// <remarks>
/// Ключ новый в 2.0, его читают только установщик и демон; панель 1.2.x его не знает и не
/// теряет. Менять его из браузера нельзя: открыть панель интернету решает тот, кто её ставит.
/// </remarks>
public enum FirewallScope
{
	/// <summary>Только локальная сеть: Windows <c>LocalSubnet</c>, в ufw частные диапазоны.</summary>
	Lan,

	/// <summary>Любой адрес, включая интернет.</summary>
	Internet,

	/// <summary>Брандмауэр не трогается: правила ведёт оператор.</summary>
	Off,
}

public static class FirewallScopeWire
{
	public static string ToWire(FirewallScope scope) => scope switch
	{
		FirewallScope.Internet => "internet",
		FirewallScope.Off => "off",
		_ => "lan",
	};

	public static FirewallScope? FromWire(string? text) => text?.Trim() switch
	{
		"lan" => FirewallScope.Lan,
		"internet" => FirewallScope.Internet,
		"off" => FirewallScope.Off,
		_ => null,
	};
}

/// <summary>
/// Где слушает панель и что из этого следует: видна ли она за пределами машины, открывать ли
/// порт в брандмауэре и по каким адресам к ней идти.
/// </summary>
/// <remarks>
/// Одно суждение для установщика, демона и мастера. Раньше проверка «адрес — петля» была
/// переписана в каждом месте вывода.
/// </remarks>
public sealed record PanelListener
{
	public const string FirewallKey = "PANEL_FIREWALL";

	/// <summary>Правило брандмауэра Windows. Порт в имя не входит: при смене порта правило одно.</summary>
	public const string WindowsRuleName = "Vintage Story Panel";

	/// <summary>
	/// Частные диапазоны IPv4 — приближение «локальной подсети» для ufw, который не знает
	/// <c>LocalSubnet</c>.
	/// </summary>
	public static IReadOnlyList<string> PrivateNetworks { get; } = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"];

	/// <summary>Частные и локальные для канала диапазоны IPv6: нужны, только если панель слушает <c>::</c>.</summary>
	public static IReadOnlyList<string> PrivateNetworks6 { get; } = ["fc00::/7", "fe80::/10"];

	private PanelListener(string bind, int port, FirewallScope firewall)
	{
		Bind = bind;
		Port = port;
		Firewall = firewall;
	}

	public string Bind { get; }

	public int Port { get; }

	public FirewallScope Firewall { get; }

	/// <summary>
	/// Настройки из файла окружения. Умолчания — как у панели, если ключа нет: петля и 8080;
	/// брандмауэр — локальная сеть.
	/// </summary>
	public static PanelListener FromEnvironment(IReadOnlyDictionary<string, string> values)
	{
		ArgumentNullException.ThrowIfNull(values);
		string Value(string key, string fallback) => values.TryGetValue(key, out var value) && value.Trim().Length > 0 ? value.Trim() : fallback;

		var port = int.TryParse(Value("PANEL_PORT", "8080"), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 8080;
		return new PanelListener(Value("PANEL_BIND", "127.0.0.1"), port, FirewallScopeWire.FromWire(Value(FirewallKey, "lan")) ?? FirewallScope.Lan);
	}

	public static PanelListener Create(string bind, int port, FirewallScope firewall) => new(bind, port, firewall);

	/// <summary>Видна ли панель за пределами машины, как <c>published()</c> 1.2.x.</summary>
	public bool Published => !IsLoopback(Bind);

	/// <summary>Слушает ли панель все адреса машины.</summary>
	public bool Wildcard => Bind is "0.0.0.0" or "::";

	/// <summary>Нужно ли правило брандмауэра: панель видна снаружи, и оператор не взял правила на себя.</summary>
	public bool OpensFirewall => Published && Firewall != FirewallScope.Off;

	/// <summary>Адрес на самой машине: для браузера рядом, ярлыка и проверки порта.</summary>
	public string LocalUrl => Url(Wildcard ? "127.0.0.1" : Host(Bind));

	/// <summary>
	/// Адреса, по которым панель открывается с других машин. Если панель слушает все адреса —
	/// по адресу на каждом интерфейсе машины, кроме петли и адресов без настроенной сети;
	/// если один адрес — только он; петля снаружи недоступна вовсе.
	/// </summary>
	/// <param name="machine">Адреса интерфейсов машины.</param>
	public IReadOnlyList<string> NetworkUrls(IEnumerable<IPAddress> machine)
	{
		ArgumentNullException.ThrowIfNull(machine);
		if (!Published)
		{
			return [];
		}

		if (!Wildcard)
		{
			return [Url(Host(Bind))];
		}

		return
		[
			.. machine
				.Where(address => !IPAddress.IsLoopback(address))
				.Where(address => address.AddressFamily == AddressFamily.InterNetwork
					? !address.ToString().StartsWith("169.254.", StringComparison.Ordinal)
					: Bind == "::" && address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6LinkLocal)
				.OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
				.Select(address => Url(address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString()))
				.Distinct(StringComparer.Ordinal),
		];
	}

	/// <summary>Аргументы <c>ufw</c> для каждого правила: пусто, если правил не нужно.</summary>
	public IReadOnlyList<IReadOnlyList<string>> UfwAllow
	{
		get
		{
			if (!OpensFirewall)
			{
				return [];
			}

			var port = Port.ToString(CultureInfo.InvariantCulture);
			if (Firewall == FirewallScope.Internet)
			{
				return [["allow", port + "/tcp"]];
			}

			var networks = Bind == "::" ? PrivateNetworks.Concat(PrivateNetworks6) : PrivateNetworks;
			return [.. networks.Select(network => (IReadOnlyList<string>) ["allow", "from", network, "to", "any", "port", port, "proto", "tcp"])];
		}
	}

	/// <summary>Аргументы <c>ufw</c>, снимающие правила <see cref="UfwAllow"/>.</summary>
	public IReadOnlyList<IReadOnlyList<string>> UfwDelete => [.. UfwAllow.Select(rule => (IReadOnlyList<string>) ["delete", .. rule])];

	/// <summary>Аргументы <c>netsh</c>, добавляющие правило; <see langword="null"/>, если правило не нужно.</summary>
	public IReadOnlyList<string>? NetshAdd => OpensFirewall
		?
		[
			"advfirewall", "firewall", "add", "rule", $"name={WindowsRuleName}", "dir=in", "action=allow", "protocol=TCP",
			$"localport={Port.ToString(CultureInfo.InvariantCulture)}",
			Firewall == FirewallScope.Internet ? "remoteip=any" : "remoteip=LocalSubnet",
		]
		: null;

	/// <summary>Аргументы <c>netsh</c>, снимающие правило панели, каким бы ни был порт.</summary>
	public static IReadOnlyList<string> NetshDelete { get; } = ["advfirewall", "firewall", "delete", "rule", $"name={WindowsRuleName}"];

	/// <summary>Кому открыт порт — для вывода оператору.</summary>
	public string FirewallDescription => Firewall switch
	{
		FirewallScope.Internet => "любой адрес, включая интернет",
		FirewallScope.Off => "правила брандмауэра не меняются",
		_ => "только локальная сеть",
	};

	internal static bool IsLoopback(string bind) => bind is "127.0.0.1" or "::1" or "localhost";

	private static string Host(string bind) => bind.Contains(':', StringComparison.Ordinal) ? $"[{bind}]" : bind;

	private string Url(string host) => $"http://{host}:{Port.ToString(CultureInfo.InvariantCulture)}/";
}
