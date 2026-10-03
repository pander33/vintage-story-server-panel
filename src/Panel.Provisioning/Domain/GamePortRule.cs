using System.Globalization;

namespace Panel.Provisioning.Domain;

/// <summary>
/// Правило брандмауэра для игрового порта сервера: TCP и UDP, для всех адресов.
/// </summary>
/// <remarks>
/// <para>
/// Одно суждение для установщика и демона: правило первого сервера ставит
/// установщик, правила серверов из панели и смену порта ведёт демон, и снимать они должны одно и то же.
/// </para>
/// <para>
/// Игровой порт открывается всем, а не локальной сети, как порт панели: игроки приходят
/// откуда угодно, а дома в интернет его всё равно выпускает только проброс на роутере.
/// </para>
/// </remarks>
public static class GamePortRule
{
	/// <summary>Протоколы игры: соединение идёт по TCP, UDP нужен для обмена данными в игре.</summary>
	public static IReadOnlyList<string> Protocols { get; } = ["tcp", "udp"];

	/// <summary>Аргументы <c>ufw</c>, открывающие порт.</summary>
	public static IReadOnlyList<IReadOnlyList<string>> UfwAllow(int port) =>
		[.. Protocols.Select(protocol => (IReadOnlyList<string>) ["allow", $"{Text(port)}/{protocol}"])];

	/// <summary>Аргументы <c>ufw</c>, снимающие правила <see cref="UfwAllow"/>.</summary>
	public static IReadOnlyList<IReadOnlyList<string>> UfwDelete(int port) =>
		[.. UfwAllow(port).Select(rule => (IReadOnlyList<string>) ["delete", .. rule])];

	/// <summary>
	/// Аргументы <c>netsh</c>, добавляющие правила. Имя с портом и протоколом —
	/// <c>Vintage Story 42420 TCP</c>: по нему правило снимается при смене порта.
	/// </summary>
	public static IReadOnlyList<IReadOnlyList<string>> NetshAdd(int port) =>
	[
		.. Protocols.Select(protocol => (IReadOnlyList<string>)
		[
			"advfirewall", "firewall", "add", "rule", $"name={WindowsRuleName(port, protocol)}", "dir=in", "action=allow",
			$"protocol={protocol.ToUpperInvariant()}", $"localport={Text(port)}",
		]),
	];

	/// <summary>Аргументы <c>netsh</c>, снимающие правила <see cref="NetshAdd"/>.</summary>
	public static IReadOnlyList<IReadOnlyList<string>> NetshDelete(int port) =>
	[
		.. Protocols.Select(protocol => (IReadOnlyList<string>)
			["advfirewall", "firewall", "delete", "rule", $"name={WindowsRuleName(port, protocol)}"]),
	];

	/// <summary>Допустимый порт игрового сервера; ноль и чужое значение правила не получают.</summary>
	public static bool IsValid(int port) => port is >= 1024 and <= 65535;

	private static string WindowsRuleName(int port, string protocol) =>
		$"Vintage Story {Text(port)} {protocol.ToUpperInvariant()}";

	private static string Text(int port) => port.ToString(CultureInfo.InvariantCulture);
}
