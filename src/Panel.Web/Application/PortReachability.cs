using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Catalogues.Infrastructure;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>Ответ внешней проверки: какой адрес проверен и открыт ли порт.</summary>
public sealed record ReachabilityResult(string PublicAddress, int Port, bool Reachable, DateTimeOffset CheckedAt, string Checker);

/// <summary>
/// Виден ли игровой порт из интернета: внешний сервис пробует подключиться к адресу, с которого
/// пришёл запрос панели.
/// </summary>
/// <remarks>
/// <para>
/// Своими силами панель этого не узнает: подключение к своему же внешнему адресу изнутри идёт
/// мимо брандмауэра провайдера (на VPS) или зависит от того, умеет ли роутер «петлю» (дома).
/// </para>
/// <para>
/// Сервис по умолчанию — <c>ifconfig.co/port/N</c> (echoip): он проверяет только адрес
/// спрашивающего, поэтому через панель нельзя прощупать чужую машину. Проверяется TCP; UDP игры
/// таким сервисом не проверить. Запрос идёт по IPv4: по нему подключается большинство игроков.
/// Сервис просит не чаще раза в минуту, поэтому ответ на порт хранится минуту, а одновременные
/// запросы ждут один и тот же.
/// </para>
/// </remarks>
public sealed class PortReachability
{
	/// <summary>Ключ <c>vs-panel.env</c>: адрес сервиса с <c>{port}</c>; <c>off</c> выключает проверку.</summary>
	public const string CheckerKey = "PANEL_PORT_CHECK_URL";

	public const string DefaultChecker = "https://ifconfig.co/port/{port}";

	private static readonly TimeSpan limit = TimeSpan.FromSeconds(20);
	private static readonly TimeSpan reuse = TimeSpan.FromMinutes(1);

	private readonly HttpClient http;
	private readonly string? template;
	private readonly TimeProvider clock;
	private readonly SemaphoreSlim gate = new(1, 1);
	private readonly ConcurrentDictionary<int, ReachabilityResult> recent = new();

	public PortReachability(HttpClient http, string? template, TimeProvider clock)
	{
		this.http = http;
		this.template = template is { Length: > 0 } value && !value.Equals("off", StringComparison.OrdinalIgnoreCase) ? value : null;
		this.clock = clock;
	}

	public bool Enabled => template is not null;

	/// <summary>Клиент, соединяющийся только по IPv4: иначе проверился бы адрес IPv6 машины.</summary>
	public static HttpClient Ipv4Client() => new(new SocketsHttpHandler
	{
		AllowAutoRedirect = false,
		ConnectCallback = async (context, ct) =>
		{
			var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
			if (addresses.Length == 0)
			{
				throw new SocketException((int) SocketError.HostNotFound);
			}

			var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
			try
			{
				await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
				return new NetworkStream(socket, ownsSocket: true);
			}
			catch
			{
				socket.Dispose();
				throw;
			}
		},
	});

	public async Task<Result<ReachabilityResult>> CheckAsync(int port, CancellationToken ct)
	{
		if (template is null)
		{
			return Failure.Unavailable($"Проверка порта из интернета выключена ({CheckerKey}=off)");
		}

		await gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			var now = clock.GetUtcNow();
			if (recent.TryGetValue(port, out var cached) && now - cached.CheckedAt < reuse)
			{
				return cached;
			}

			var target = new Uri(template.Replace("{port}", port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
			timeout.CancelAfter(limit);

			JsonObject? answer;
			try
			{
				using var request = new HttpRequestMessage(HttpMethod.Get, target);
				request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
				using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
				if (!response.IsSuccessStatusCode)
				{
					var code = ((int) response.StatusCode).ToString(CultureInfo.InvariantCulture);
					return Failure.Unavailable($"Сервис проверки {target.Host} ответил {code}");
				}

				answer = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false)) as JsonObject;
			}
			catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or SocketException && !ct.IsCancellationRequested)
			{
				return Failure.Unavailable(Reachability.Explain(exception, target, limit));
			}
			catch (JsonException)
			{
				answer = null;
			}

			if (answer?["reachable"] is not JsonValue reachable || reachable.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)
				|| answer["ip"] is not JsonValue ip || !ip.TryGetValue<string>(out var address))
			{
				return Failure.Unavailable($"Сервис проверки {target.Host} ответил не в ожидаемом формате");
			}

			var result = new ReachabilityResult(address, port, reachable.GetValue<bool>(), now, target.Host);
			recent[port] = result;
			return result;
		}
		finally
		{
			gate.Release();
		}
	}
}
