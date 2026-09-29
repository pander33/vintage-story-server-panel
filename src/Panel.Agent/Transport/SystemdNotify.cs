using System.Net.Sockets;
using System.Text;

namespace Panel.Agent.Transport;

/// <summary>
/// Сообщение готовности для юнита <c>Type=notify</c> (<c>sd_notify(3)</c>).
/// </summary>
/// <remarks>
/// С <c>Type=exec</c> <c>systemctl restart</c> возвращался до <c>bind</c>, и установщик
/// обращался к демону раньше, чем появлялся сокет с нужными правами. Библиотека хостинга ради
/// одной датаграммы не берётся: у привилегированной стороны ровно одна сторонняя зависимость.
/// Без <c>NOTIFY_SOCKET</c> (запуск из терминала) ничего не делает.
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public static class SystemdNotify
{
	public static void Ready()
	{
		var path = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
		if (string.IsNullOrEmpty(path))
		{
			return;
		}

		// Абстрактное имя systemd пишет с '@', в адресе сокета это нулевой байт.
		if (path[0] == '@')
		{
			path = "\0" + path[1..];
		}

		try
		{
			using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
			socket.Connect(new UnixDomainSocketEndPoint(path));
			socket.Send(Encoding.ASCII.GetBytes("READY=1"));
		}
		catch (SocketException exception)
		{
			// systemd не дождётся готовности и остановит юнит по TimeoutStartSec: это видно в журнале.
			Console.Error.WriteLine($"Готовность не сообщена systemd: {exception.Message}");
		}
	}
}
