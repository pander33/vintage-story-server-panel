using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace Panel.Web.Middleware;

/// <summary>
/// Адрес посетителя за обратным прокси (<c>PANEL_TRUSTED_PROXIES</c>).
/// </summary>
/// <remarks>
/// <c>X-Forwarded-For</c> читается справа налево, пока адрес соединения — доверенный прокси:
/// цепочка прокси работает, а клиент, пришедший напрямую, заголовком свой адрес не подменит.
/// От адреса зависят ограничитель попыток входа и аудит. Умолчания ASP.NET (петля) заменяются
/// списком целиком. Host и схема не переносятся: Host сверяет фильтр хостов, и прокси передаёт
/// его как есть.
/// </remarks>
public static class ProxyTrust
{
	public static void Configure(ForwardedHeadersOptions forwarded, IReadOnlyList<System.Net.IPNetwork> proxies)
	{
		ArgumentNullException.ThrowIfNull(forwarded);
		ArgumentNullException.ThrowIfNull(proxies);

		forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
		forwarded.ForwardLimit = null;
		forwarded.KnownProxies.Clear();
		forwarded.KnownIPNetworks.Clear();
		foreach (var network in proxies)
		{
			forwarded.KnownIPNetworks.Add(network);
		}
	}
}
