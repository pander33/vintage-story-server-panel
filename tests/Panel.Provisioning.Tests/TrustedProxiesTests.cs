using System.Net;
using Panel.Provisioning.Domain;

namespace Panel.Provisioning.Tests;

/// <summary><c>PANEL_TRUSTED_PROXIES</c>: адреса и сети обратных прокси.</summary>
public sealed class TrustedProxiesTests
{
	[Fact]
	public void Умолчание_петля_IPv4_и_IPv6()
	{
		var proxies = TrustedProxies.Parse(TrustedProxies.Default).Value;

		Assert.Contains(proxies, network => network.Contains(IPAddress.Loopback));
		Assert.Contains(proxies, network => network.Contains(IPAddress.IPv6Loopback));
		Assert.DoesNotContain(proxies, network => network.Contains(IPAddress.Parse("127.0.0.2")));
	}

	[Fact]
	public void Адрес_и_сеть_через_запятую()
	{
		var proxies = TrustedProxies.Parse(" 10.0.0.5 , 172.16.0.0/12,fd00::/8 ").Value;

		Assert.Equal(3, proxies.Count);
		Assert.True(proxies[0].Contains(IPAddress.Parse("10.0.0.5")));
		Assert.False(proxies[0].Contains(IPAddress.Parse("10.0.0.6")));
		Assert.True(proxies[1].Contains(IPAddress.Parse("172.31.255.1")));
		Assert.True(proxies[2].Contains(IPAddress.Parse("fd12::1")));
	}

	[Theory]
	[InlineData("")]
	[InlineData(" , ")]
	[InlineData("proxy.example.com")]
	[InlineData("10.0.0.1/8")]
	[InlineData("192.168.1.10/8")]
	[InlineData("fd00::1/8")]
	[InlineData("10.0.0.0/33")]
	[InlineData("127.0.0.1,*")]
	public void Ошибочная_запись_отвергает_весь_список(string value)
	{
		Assert.False(TrustedProxies.Parse(value).IsSuccess);
	}
}
