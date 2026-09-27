using System.Net;
using Panel.Provisioning.Domain;

namespace Panel.Provisioning.Tests;

/// <summary>Где слушает панель, кому открыт порт и по каким адресам её искать.</summary>
public sealed class PanelListenerTests
{
	private static PanelListener From(params (string Key, string Value)[] values) =>
		PanelListener.FromEnvironment(values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));

	[Fact]
	public void Без_ключей_умолчания_панели_петля_и_локальная_сеть()
	{
		var listener = From();

		Assert.Equal("127.0.0.1", listener.Bind);
		Assert.Equal(8080, listener.Port);
		Assert.Equal(FirewallScope.Lan, listener.Firewall);
		Assert.False(listener.Published);
		Assert.False(listener.OpensFirewall);
	}

	[Theory]
	[InlineData("127.0.0.1")]
	[InlineData("::1")]
	[InlineData("localhost")]
	public void Петля_порт_не_открывает(string bind)
	{
		var listener = From(("PANEL_BIND", bind), (PanelListener.FirewallKey, "internet"));

		Assert.False(listener.OpensFirewall);
		Assert.Empty(listener.UfwAllow);
		Assert.Null(listener.NetshAdd);
		Assert.Empty(listener.NetworkUrls([IPAddress.Parse("192.168.1.10")]));
	}

	[Fact]
	public void Локальная_сеть_в_ufw_это_частные_диапазоны()
	{
		var listener = From(("PANEL_BIND", "0.0.0.0"), ("PANEL_PORT", "9000"));

		Assert.Equal(
			[
				"allow from 10.0.0.0/8 to any port 9000 proto tcp",
				"allow from 172.16.0.0/12 to any port 9000 proto tcp",
				"allow from 192.168.0.0/16 to any port 9000 proto tcp",
			],
			listener.UfwAllow.Select(rule => string.Join(" ", rule)));
		Assert.All(listener.UfwDelete, rule => Assert.Equal("delete", rule[0]));
		Assert.Contains("remoteip=LocalSubnet", listener.NetshAdd!);
	}

	[Fact]
	public void Интернет_открывает_порт_всем()
	{
		var listener = From(("PANEL_BIND", "0.0.0.0"), (PanelListener.FirewallKey, "internet"));

		Assert.Equal(["allow 8080/tcp"], listener.UfwAllow.Select(rule => string.Join(" ", rule)));
		Assert.Contains("remoteip=any", listener.NetshAdd!);
		Assert.Contains("localport=8080", listener.NetshAdd!);
	}

	[Fact]
	public void IPv6_диапазоны_только_когда_панель_слушает_IPv6()
	{
		Assert.DoesNotContain(From(("PANEL_BIND", "0.0.0.0")).UfwAllow, rule => rule.Contains("fc00::/7"));
		Assert.Contains(From(("PANEL_BIND", "::")).UfwAllow, rule => rule.Contains("fc00::/7"));
	}

	[Fact]
	public void Off_оставляет_брандмауэр_оператору()
	{
		var listener = From(("PANEL_BIND", "0.0.0.0"), (PanelListener.FirewallKey, "off"));

		Assert.True(listener.Published);
		Assert.False(listener.OpensFirewall);
		Assert.Null(listener.NetshAdd);
	}

	[Fact]
	public void Неизвестный_охват_считается_локальной_сетью()
	{
		Assert.Equal(FirewallScope.Lan, From((PanelListener.FirewallKey, "everyone")).Firewall);
	}

	[Fact]
	public void Правило_Windows_одно_какой_бы_ни_был_порт()
	{
		var first = From(("PANEL_BIND", "0.0.0.0"), ("PANEL_PORT", "8080")).NetshAdd!;
		var second = From(("PANEL_BIND", "0.0.0.0"), ("PANEL_PORT", "9000")).NetshAdd!;

		Assert.Equal(first.Single(item => item.StartsWith("name=", StringComparison.Ordinal)), second.Single(item => item.StartsWith("name=", StringComparison.Ordinal)));
		Assert.Contains($"name={PanelListener.WindowsRuleName}", PanelListener.NetshDelete);
	}

	[Fact]
	public void Адреса_в_сети_без_петли_и_без_адресов_без_сети()
	{
		var listener = From(("PANEL_BIND", "0.0.0.0"), ("PANEL_PORT", "8080"));

		var urls = listener.NetworkUrls(
		[
			IPAddress.Loopback, IPAddress.Parse("169.254.10.1"), IPAddress.Parse("192.168.1.10"), IPAddress.Parse("fd00::1"), IPAddress.Parse("10.0.0.5"),
		]);

		Assert.Equal(["http://192.168.1.10:8080/", "http://10.0.0.5:8080/"], urls);
		Assert.Equal("http://127.0.0.1:8080/", listener.LocalUrl);
	}

	[Fact]
	public void Адреса_IPv6_показываются_когда_панель_слушает_IPv6()
	{
		var urls = From(("PANEL_BIND", "::")).NetworkUrls([IPAddress.Parse("192.168.1.10"), IPAddress.Parse("fd00::1"), IPAddress.Parse("fe80::1")]);

		Assert.Equal(["http://192.168.1.10:8080/", "http://[fd00::1]:8080/"], urls);
	}

	[Fact]
	public void Один_адрес_это_он_и_есть()
	{
		var listener = From(("PANEL_BIND", "192.168.1.10"));

		Assert.Equal(["http://192.168.1.10:8080/"], listener.NetworkUrls([IPAddress.Parse("10.0.0.5")]));
		Assert.Equal("http://192.168.1.10:8080/", listener.LocalUrl);
	}
}
