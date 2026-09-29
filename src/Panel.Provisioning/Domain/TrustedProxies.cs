using System.Net;
using System.Net.Sockets;
using Panel.SharedKernel;

namespace Panel.Provisioning.Domain;

/// <summary>
/// Ключ <c>PANEL_TRUSTED_PROXIES</c> в <c>vs-panel.env</c>: адреса и сети обратных прокси, чьему
/// <c>X-Forwarded-For</c> панель верит.
/// </summary>
/// <remarks>
/// <para>
/// Доверие — свойство соединения, а не заголовка: адрес посетителя из заголовка берётся, только
/// если само соединение пришло от прокси из списка. Иначе любой клиент назвался бы чужим адресом
/// и обошёл ограничитель попыток входа, а в аудит попал бы выдуманный адрес.
/// </para>
/// <para>
/// Умолчание — петля: прокси на той же машине. Прокси на другой машине (роутер, отдельный nginx)
/// называется явно, адресом или сетью. Задаёт ключ установщик; как и <c>PANEL_FIREWALL</c>, из
/// браузера он не меняется.
/// </para>
/// </remarks>
public static class TrustedProxies
{
	public const string Key = "PANEL_TRUSTED_PROXIES";

	/// <summary>Петля IPv4 и IPv6.</summary>
	public const string Default = "127.0.0.1,::1";

	/// <summary>
	/// Список через запятую: адреса (<c>10.0.0.5</c>, <c>::1</c>) и сети (<c>10.0.0.0/8</c>).
	/// Одна ошибочная запись отвергает весь список: доверие, заданное наполовину, хуже отказа.
	/// </summary>
	public static Result<IReadOnlyList<IPNetwork>> Parse(string value)
	{
		var entries = (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		if (entries.Length == 0)
		{
			return Failure.Invalid($"{Key}: expected comma-separated addresses or networks, such as 127.0.0.1,10.0.0.0/8.");
		}

		var networks = new List<IPNetwork>(entries.Length);
		foreach (var entry in entries)
		{
			// TryParse обнуляет биты узла сам: 192.168.1.10/8 дал бы 192.0.0.0/8. Опечатка не должна
			// молча расширять доверие, поэтому адрес сети обязан совпасть с написанным.
			if (entry.Contains('/', StringComparison.Ordinal)
				&& IPNetwork.TryParse(entry, out var network)
				&& IPAddress.TryParse(entry[..entry.IndexOf('/', StringComparison.Ordinal)], out var written)
				&& written.Equals(network.BaseAddress))
			{
				networks.Add(network);
			}
			else if (IPAddress.TryParse(entry, out var address) && !entry.Contains('/', StringComparison.Ordinal))
			{
				networks.Add(new IPNetwork(address, address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32));
			}
			else
			{
				return Failure.Invalid($"{Key}: '{entry}' is neither an IP address nor a network such as 10.0.0.0/8.");
			}
		}

		return Result<IReadOnlyList<IPNetwork>>.Ok(networks);
	}
}
