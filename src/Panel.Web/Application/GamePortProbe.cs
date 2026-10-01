using System.Net;
using System.Net.Sockets;
using Panel.Runtime.Ports;

namespace Panel.Web.Application;

/// <summary>
/// Пробная привязка к игровому порту по TCP и UDP; сокеты сразу закрываются.
/// </summary>
/// <remarks>
/// TCP-сокет ещё и слушает: на Linux привязка с <c>SO_REUSEADDR</c> может пройти, а вот
/// <c>listen</c> на занятом порту — нет. Для UDP такой проверки нет, и сокет игры с
/// <c>SO_REUSEADDR</c> останется незамеченным: проверка находит не всё, но ложного «занято» не даёт.
/// </remarks>
public sealed class GamePortProbe : IGamePortProbe
{
	public PortAvailability Probe(IPAddress? address, int port)
	{
		var endpoint = new IPEndPoint(address ?? IPAddress.Any, port);
		try
		{
			using (var tcp = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
			{
				tcp.Bind(endpoint);
				tcp.Listen(1);
			}

			using (var udp = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
			{
				udp.Bind(endpoint);
			}

			return PortAvailability.Free;
		}
		catch (SocketException exception)
		{
			return exception.SocketErrorCode switch
			{
				SocketError.AddressAlreadyInUse => PortAvailability.InUse,
				SocketError.AccessDenied => PortAvailability.Denied,
				_ => PortAvailability.Unknown,
			};
		}
	}
}
