using System.Net;
using System.Net.Sockets;
using System.Text;
using Panel.Catalogues.Infrastructure;

namespace Panel.Catalogues.Tests;

/// <summary>
/// Узел CDN из DNS может молчать: из РФ ModDB отдавал заблокированный узел, а соседний работал.
/// </summary>
public sealed class FallbackConnectorTests
{
	private static readonly IPAddress dns = IPAddress.Parse("169.150.247.38");
	private static readonly IPAddress reserve = IPAddress.Parse("79.127.134.133");

	[Fact]
	public void Сначала_последний_удачный_потом_DNS_потом_резерв_без_повторов()
	{
		Assert.Equal([reserve, dns], FallbackConnector.Order(reserve, [dns, reserve], [reserve]));
		Assert.Equal([dns, reserve], FallbackConnector.Order(null, [dns], [reserve]));
	}

	[Fact]
	public async Task Молчащий_адрес_из_DNS_сменяется_резервным()
	{
		using var server = new TcpListener(IPAddress.Loopback, 0);
		server.Start();
		var port = ((IPEndPoint) server.LocalEndpoint).Port;
		var answered = AnswerAsync(server);

		// Слушает только IPv4: соединение с ::1 на этом порту отклоняется.
		using var client = Client(new Dictionary<string, IReadOnlyList<IPAddress>> { ["cdn.test"] = [IPAddress.Loopback] }, [IPAddress.IPv6Loopback]);
		var response = await client.GetStringAsync(new Uri($"http://cdn.test:{port}/"));

		Assert.Equal("ok", response);
		await answered;
	}

	[Fact]
	public async Task Ни_один_адрес_не_ответил_и_причина_называет_все()
	{
		using var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		var port = ((IPEndPoint) probe.LocalEndpoint).Port;
		probe.Stop();

		using var client = Client(new Dictionary<string, IReadOnlyList<IPAddress>> { ["cdn.test"] = [IPAddress.Loopback] }, [IPAddress.IPv6Loopback]);
		var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync(new Uri($"http://cdn.test:{port}/")));

		var unreachable = Assert.IsType<HostUnreachableException>(failure.InnerException);
		Assert.Equal([IPAddress.IPv6Loopback, IPAddress.Loopback], unreachable.Tried);
	}

	[Fact]
	public void Недоступный_CDN_объясняется_с_ключом_резерва()
	{
		var failure = new HttpRequestException(HttpRequestError.ConnectionError, "x", new HostUnreachableException(ModDbClient.DownloadHost, [dns], null));

		var phrase = Reachability.Explain(failure, new Uri("https://moddbcdn.vintagestory.at/a.png"), TimeSpan.FromSeconds(10));

		Assert.Contains("заблокирован", phrase.Template, StringComparison.Ordinal);
		Assert.Equal([ModDbClient.DownloadHost, "169.150.247.38", Reachability.CdnAddressesKey], phrase.Args);
	}

	[Fact]
	public void Истёкшее_ожидание_называет_узел_и_предел()
	{
		var phrase = Reachability.Explain(new TaskCanceledException(), new Uri(ModDbClient.ApiBase), TimeSpan.FromSeconds(15));

		Assert.Equal("{0} не ответил за {1} с", phrase.Template);
		Assert.Equal(["mods.vintagestory.at", "15"], phrase.Args);
	}

	/// <summary>Без прокси из окружения: иначе соединение шло бы к нему, а не к узлу.</summary>
	private static HttpClient Client(Dictionary<string, IReadOnlyList<IPAddress>> fallbacks, IPAddress[] resolved)
	{
		var handler = new FallbackConnector(fallbacks, (_, _) => Task.FromResult(resolved)).Handler(redirects: false);
		handler.UseProxy = false;
		return new HttpClient(handler);
	}

	private static async Task AnswerAsync(TcpListener server)
	{
		using var connection = await server.AcceptTcpClientAsync();
		var stream = connection.GetStream();
		var buffer = new byte[4096];
		_ = await stream.ReadAsync(buffer);
		await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
	}
}
