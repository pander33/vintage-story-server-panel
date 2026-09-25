using System.Text.RegularExpressions;

namespace Panel.Contracts.Privacy;

/// <summary>
/// Сокрытие сетевых адресов, одно на всю панель.
/// </summary>
/// <remarks>
/// <para>
/// Наблюдатель видит активность игроков, но не их адреса. Мест вывода много: таблица
/// игроков, поток телеметрии, журналы, выгрузка журнала, лента чата. Если бы каждое скрывало
/// адреса само, новое место вывода оказалось бы незащищённым.
/// </para>
/// <para>
/// Сервер пишет адрес в скобках (<c>Alice [::1]:1234 joins.</c>), и скобки входят в
/// совпадение, иначе получилось бы <c>[[скрыт]]</c>. Порт остаётся: по нему игрока не найти, а
/// без него строку журнала не узнать. Адрес IPv6 обязан содержать двоеточие, иначе
/// скрывались бы и теги модов вроде <c>[cafe]</c>.
/// </para>
/// </remarks>
public static partial class AddressMask
{
	/// <summary>Замена адреса; скобки входят в замену.</summary>
	public const string Hidden = "[скрыт]";

	[GeneratedRegex(
		@"\[(?:\d{1,3}\.){3}\d{1,3}\]" +
		@"|\[[0-9A-Fa-f:]*:[0-9A-Fa-f:]*\]" +
		@"|\b(?:\d{1,3}\.){3}\d{1,3}\b")]
	private static partial Regex Address { get; }

	/// <summary>
	/// Значение, которое целиком является адресом: наблюдатель получает <c>null</c>, а не заглушку.
	/// </summary>
	public static string? Apply(string? value, bool hide) => hide ? null : value;

	/// <summary>Убрать адреса из текста: строки журнала или чата.</summary>
	public static string Scrub(string line, bool hide) =>
		!hide || string.IsNullOrEmpty(line) ? line : Address.Replace(line, Hidden);
}
