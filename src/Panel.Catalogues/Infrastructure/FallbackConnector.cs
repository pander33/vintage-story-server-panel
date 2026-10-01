using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Panel.Catalogues.Infrastructure;

/// <summary>Узел не принял соединение ни по одному адресу — ни из DNS, ни из резервных.</summary>
public sealed class HostUnreachableException(string host, IReadOnlyList<IPAddress> tried, Exception? inner)
	: IOException(string.Create(CultureInfo.InvariantCulture, $"No connection to {host} ({string.Join(", ", tried)})"), inner)
{
	public string Host { get; } = host;

	public IReadOnlyList<IPAddress> Tried { get; } = tried;
}

/// <summary>
/// Соединение с внешними каталогами, которое не зависит от одного адреса из DNS.
/// </summary>
/// <remarks>
/// DNS отдаёт узел CDN по месту запроса, и провайдер может не пропускать именно его: из РФ
/// <c>moddbcdn.vintagestory.at</c> отвечал одним адресом, который молчал, а соседний узел того же CDN
/// работал. Поэтому адреса пробуются по очереди с коротким пределом на каждый: сначала тот, что
/// сработал последним, затем из DNS, затем резервные для этого узла. Удачный запоминается, и
/// следующие запросы не ждут молчащий. TLS проверяется по имени узла как обычно: подменяется
/// только адрес, сертификат остаётся сертификатом CDN.
/// </remarks>
public sealed class FallbackConnector
{
	/// <summary>Предел одной попытки: меньше пределов запросов, чтобы успел и резервный адрес.</summary>
	public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(4);

	private readonly IReadOnlyDictionary<string, IReadOnlyList<IPAddress>> fallbacks;
	private readonly Func<string, CancellationToken, Task<IPAddress[]>> resolve;
	private readonly ConcurrentDictionary<string, IPAddress> working = new(StringComparer.OrdinalIgnoreCase);

	/// <param name="fallbacks">Резервные адреса по имени узла.</param>
	/// <param name="resolve">Адреса узла; по умолчанию — DNS.</param>
	public FallbackConnector(
		IReadOnlyDictionary<string, IReadOnlyList<IPAddress>> fallbacks,
		Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
	{
		ArgumentNullException.ThrowIfNull(fallbacks);
		this.fallbacks = new Dictionary<string, IReadOnlyList<IPAddress>>(fallbacks, StringComparer.OrdinalIgnoreCase);
		this.resolve = resolve ?? Dns.GetHostAddressesAsync;
	}

	/// <summary>Обработчик с этим соединением; прокси из окружения (<c>HTTPS_PROXY</c>) работает как прежде.</summary>
	public SocketsHttpHandler Handler(bool redirects) => new()
	{
		AllowAutoRedirect = redirects,
		ConnectCallback = ConnectAsync,
	};

	/// <summary>Порядок попыток: последний удачный, адреса DNS, резервные — без повторов.</summary>
	public static IReadOnlyList<IPAddress> Order(IPAddress? last, IReadOnlyList<IPAddress> resolved, IReadOnlyList<IPAddress> reserve)
	{
		ArgumentNullException.ThrowIfNull(resolved);
		ArgumentNullException.ThrowIfNull(reserve);
		IEnumerable<IPAddress> all = last is null ? [.. resolved, .. reserve] : [last, .. resolved, .. reserve];
		return [.. all.Distinct()];
	}

	private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
	{
		var host = context.DnsEndPoint.Host;
		var reserve = fallbacks.GetValueOrDefault(host) ?? [];

		IPAddress[] resolved;
		try
		{
			resolved = await resolve(host, ct).ConfigureAwait(false);
		}
		catch (SocketException) when (reserve.Count > 0)
		{
			// DNS не ответил, но адрес узла известен заранее.
			resolved = [];
		}

		var candidates = Order(working.GetValueOrDefault(host), resolved, reserve);
		Exception? last = null;
		foreach (var address in candidates)
		{
			var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
			using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
			attempt.CancelAfter(AttemptTimeout);
			try
			{
				await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), attempt.Token).ConfigureAwait(false);
				working[host] = address;
				return new NetworkStream(socket, ownsSocket: true);
			}
			catch (Exception exception) when (exception is SocketException
												  || (exception is OperationCanceledException && !ct.IsCancellationRequested))
			{
				socket.Dispose();
				working.TryRemove(KeyValuePair.Create(host, address));
				last = exception;
			}
			catch
			{
				socket.Dispose();
				throw;
			}
		}

		throw new HostUnreachableException(host, candidates, last);
	}
}
