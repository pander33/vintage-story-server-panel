using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Panel.Provisioning.Domain;
using Panel.Web.Endpoints;
using Panel.Web.Middleware;

namespace Panel.Web.Tests;

/// <summary>
/// Адрес посетителя за обратным прокси: от него зависят ограничитель попыток входа и аудит.
/// </summary>
public sealed class ProxyTrustTests
{
	private static async Task<string> ClientAddress(string proxies, string connection, string? forwardedFor)
	{
		var forwarded = new ForwardedHeadersOptions();
		ProxyTrust.Configure(forwarded, TrustedProxies.Parse(proxies).Value);

		var address = string.Empty;
		var middleware = new ForwardedHeadersMiddleware(
			http =>
			{
				address = PanelResults.ClientAddress(http);
				return Task.CompletedTask;
			},
			NullLoggerFactory.Instance,
			Options.Create(forwarded));

		var context = new DefaultHttpContext();
		context.Connection.RemoteIpAddress = IPAddress.Parse(connection);
		if (forwardedFor is not null)
		{
			context.Request.Headers["X-Forwarded-For"] = forwardedFor;
		}

		await middleware.Invoke(context);
		return address;
	}

	[Fact]
	public async Task Прокси_на_той_же_машине_передаёт_адрес_посетителя()
	{
		Assert.Equal("203.0.113.7", await ClientAddress(TrustedProxies.Default, "127.0.0.1", "203.0.113.7"));
	}

	[Fact]
	public async Task Клиент_напрямую_не_подменяет_свой_адрес()
	{
		Assert.Equal("198.51.100.9", await ClientAddress(TrustedProxies.Default, "198.51.100.9", "127.0.0.1"));
	}

	[Fact]
	public async Task Прокси_на_другой_машине_доверен_только_по_списку()
	{
		Assert.Equal("10.0.0.5", await ClientAddress(TrustedProxies.Default, "10.0.0.5", "203.0.113.7"));
		Assert.Equal("203.0.113.7", await ClientAddress("10.0.0.0/8", "10.0.0.5", "203.0.113.7"));
	}

	/// <summary>
	/// Цепочка читается справа налево до первого недоверенного адреса: запись, которую
	/// посетитель дописал в заголовок сам, остаётся левее его настоящего адреса.
	/// </summary>
	[Fact]
	public async Task Цепочка_прокси_останавливается_на_первом_чужом_адресе()
	{
		Assert.Equal("203.0.113.7", await ClientAddress("127.0.0.1,10.0.0.0/8", "127.0.0.1", "1.2.3.4, 203.0.113.7, 10.0.0.5"));
	}

	[Fact]
	public async Task Без_заголовка_адрес_соединения()
	{
		Assert.Equal("127.0.0.1", await ClientAddress(TrustedProxies.Default, "127.0.0.1", null));
	}
}
