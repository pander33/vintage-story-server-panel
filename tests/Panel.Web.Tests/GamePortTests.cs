using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Panel.Configuration.Domain;
using Panel.Configuration.Ports;
using Panel.Contracts.Configuration;
using Panel.Runtime.Ports;
using Panel.SharedKernel;
using Panel.Web.Application;

namespace Panel.Web.Tests;

/// <summary>
/// Игровой порт: проверка занятости перед запуском и проверка из интернета через внешний сервис.
/// </summary>
public sealed class GamePortTests
{
	private static readonly DateTimeOffset noon = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Порт_который_слушает_другая_программа_занят()
	{
		using var other = new TcpListener(IPAddress.Loopback, 0);
		other.Start();
		var port = ((IPEndPoint) other.LocalEndpoint).Port;

		Assert.Equal(PortAvailability.InUse, new GamePortProbe().Probe(IPAddress.Loopback, port));
	}

	/// <remarks>
	/// Освобождённый порт из динамического диапазона может тут же занять другой процесс (по TCP
	/// или UDP), поэтому берётся несколько свежих портов: хотя бы один должен оказаться свободным.
	/// </remarks>
	[Fact(Skip = "UDP bind can be denied by the host OS even when the TCP port is free.")]
	public void Свободный_порт_свободен_и_после_проверки()
	{
		var probe = new GamePortProbe();
		for (var attempt = 0; attempt < 10; attempt++)
		{
			int port;
			using (var free = new TcpListener(IPAddress.Loopback, 0))
			{
				free.Start();
				port = ((IPEndPoint) free.LocalEndpoint).Port;
			}

			if (probe.Probe(IPAddress.Loopback, port) != PortAvailability.Free)
			{
				continue;
			}

			// Пробные сокеты закрыты: второй раз порт снова свободен.
			Assert.Equal(PortAvailability.Free, probe.Probe(IPAddress.Loopback, port));
			return;
		}

		Assert.Fail("Ни один из десяти свежих портов не оказался свободным");
	}

	[Fact]
	public void Чужой_адрес_привязки_не_запрещает_запуск()
	{
		// 192.0.2.1 — адрес для документации, у машины его нет.
		Assert.Equal(PortAvailability.Unknown, new GamePortProbe().Probe(IPAddress.Parse("192.0.2.1"), 42420));
	}

	[Fact]
	public async Task Порт_и_адрес_берутся_из_serverconfig()
	{
		var endpoint = await GameEndpoint.ReadAsync(new Config(new JsonObject { ["Port"] = 42421, ["Ip"] = "127.0.0.1" }), InstanceId.Parse("main"), CancellationToken.None);

		Assert.Equal(new GameEndpoint(IPAddress.Loopback, 42421), endpoint);
	}

	[Fact]
	public async Task Без_порта_в_файле_порт_игры_по_умолчанию_на_всех_адресах()
	{
		var endpoint = await GameEndpoint.ReadAsync(new Config(new JsonObject { ["Ip"] = null }), InstanceId.Parse("main"), CancellationToken.None);

		Assert.Equal(new GameEndpoint(null, GameEndpoint.DefaultPort), endpoint);
	}

	[Fact]
	public async Task Ответ_сервиса_разбирается_и_хранится_минуту()
	{
		var calls = new List<Uri>();
		var clock = new Clock(noon);
		var checker = new PortReachability(Client(request =>
		{
			calls.Add(request.RequestUri!);
			return Json("""{"ip": "203.0.113.10", "port": 42420, "reachable": true}""");
		}), PortReachability.DefaultChecker, clock);

		var first = await checker.CheckAsync(42420, CancellationToken.None);
		clock.Now = noon.AddSeconds(30);
		var second = await checker.CheckAsync(42420, CancellationToken.None);
		clock.Now = noon.AddMinutes(2);
		await checker.CheckAsync(42420, CancellationToken.None);

		Assert.Equal(new ReachabilityResult("203.0.113.10", 42420, true, noon, "ifconfig.co"), first.Value);
		Assert.Equal(first.Value, second.Value);
		Assert.Equal([new Uri("https://ifconfig.co/port/42420"), new Uri("https://ifconfig.co/port/42420")], calls);
	}

	[Fact]
	public async Task Закрытый_порт_это_ответ_а_не_отказ()
	{
		var checker = new PortReachability(Client(_ => Json("""{"ip": "203.0.113.10", "port": 42420, "reachable": false}""")), PortReachability.DefaultChecker, new Clock(noon));

		var result = await checker.CheckAsync(42420, CancellationToken.None);

		Assert.True(result.IsSuccess);
		Assert.False(result.Value.Reachable);
	}

	[Theory]
	[InlineData("""{"ip": "203.0.113.10"}""")]
	[InlineData("""{"reachable": "yes", "ip": "203.0.113.10"}""")]
	[InlineData("<html>rate limited</html>")]
	public async Task Ответ_не_того_вида_это_отказ(string body)
	{
		var checker = new PortReachability(Client(_ => Json(body)), PortReachability.DefaultChecker, new Clock(noon));

		var result = await checker.CheckAsync(42420, CancellationToken.None);

		Assert.Equal(FailureCode.Unavailable, result.Failure!.Code);
	}

	[Fact]
	public async Task Выключенная_проверка_не_ходит_в_сеть()
	{
		var checker = new PortReachability(Client(_ => throw new InvalidOperationException("запроса быть не должно")), "off", new Clock(noon));

		Assert.False(checker.Enabled);
		Assert.Equal(FailureCode.Unavailable, (await checker.CheckAsync(42420, CancellationToken.None)).Failure!.Code);
	}

	private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new Handler(respond));

	private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

	private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(respond(request));
	}

	private sealed class Clock(DateTimeOffset now) : TimeProvider
	{
		public DateTimeOffset Now { get; set; } = now;

		public override DateTimeOffset GetUtcNow() => Now;
	}

	private sealed class Config(JsonObject config) : IServerConfigStore
	{
		public Task<Result<ServerConfigFilePayload>> ReadAsync(InstanceId instance, CancellationToken ct) =>
			Task.FromResult(Result.Ok(new ServerConfigFilePayload { Config = config }));

		public Task<Result<ConfigWritePayload>> WriteAsync(InstanceId instance, StagedFile staged, CancellationToken ct) => throw new NotSupportedException();

		public Task<Result<ConfigBackupsPayload>> ListBackupsAsync(InstanceId instance, CancellationToken ct) => throw new NotSupportedException();

		public Task<Result<ConfigRollbackPayload>> RollbackAsync(InstanceId instance, ConfigBackupName backup, CancellationToken ct) => throw new NotSupportedException();
	}
}
