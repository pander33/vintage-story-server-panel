using System.Globalization;
using System.Net.Sockets;
using Panel.SharedKernel;

namespace Panel.Catalogues.Infrastructure;

/// <summary>
/// Причина сетевого отказа словами оператора: что не так и что сделать, вместо текста исключения.
/// </summary>
public static class Reachability
{
	/// <summary>Ключ <c>vs-panel.env</c> с резервными адресами CDN ModDB.</summary>
	public const string CdnAddressesKey = "PANEL_MODDB_CDN_ADDRESSES";

	/// <summary>
	/// Объяснение отказа запроса к <paramref name="target"/>. Предел запроса нужен, чтобы назвать
	/// истёкшее ожидание; прочие отказы — текстом исключения, как раньше.
	/// </summary>
	public static Phrase Explain(Exception exception, Uri target, TimeSpan limit)
	{
		ArgumentNullException.ThrowIfNull(exception);
		ArgumentNullException.ThrowIfNull(target);
		var host = target.Host;

		if (Find<HostUnreachableException>(exception) is { } unreachable)
		{
			var addresses = string.Join(", ", unreachable.Tried);
			return string.Equals(host, ModDbClient.DownloadHost, StringComparison.OrdinalIgnoreCase)
				? Phrase.Format($"нет соединения с {host} ({addresses}): узел не отвечает, вероятно, адрес заблокирован провайдером. Укажите рабочий адрес узла в {CdnAddressesKey} или прокси в HTTPS_PROXY")
				: Phrase.Format($"нет соединения с {host} ({addresses}): узел не отвечает, вероятно, адрес заблокирован провайдером. Укажите прокси в HTTPS_PROXY");
		}

		if (Find<SocketException>(exception) is { SocketErrorCode: SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData })
		{
			return Phrase.Format($"адрес {host} не найден: проверьте DNS сервера");
		}

		if (exception is OperationCanceledException)
		{
			var seconds = ((int) limit.TotalSeconds).ToString(CultureInfo.InvariantCulture);
			return Phrase.Format($"{host} не ответил за {seconds} с");
		}

		return Phrase.From(exception.Message);
	}

	private static T? Find<T>(Exception? exception)
		where T : Exception
	{
		for (; exception is not null; exception = exception.InnerException)
		{
			if (exception is T found)
			{
				return found;
			}
		}

		return null;
	}
}
