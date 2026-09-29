namespace Panel.Web.Application;

/// <summary>Адрес панели, который мастер установки показывает ссылкой «Перейти в панель».</summary>
public static class PanelAddress
{
	/// <summary>
	/// Адрес после перезапуска. <c>0.0.0.0</c> и <c>::</c> — это «все интерфейсы», а не адрес:
	/// браузер по нему не попадёт на панель, установленную на другой машине. Тогда берётся хост,
	/// по которому браузер открыл мастер: панель слушает и его. Конкретный адрес привязки
	/// остаётся как есть — после перезапуска панель доступна только по нему.
	/// </summary>
	/// <param name="bind">PANEL_BIND после мастера.</param>
	/// <param name="port">PANEL_PORT после мастера.</param>
	/// <param name="requestHost">Хост запроса без порта; IPv6 — в скобках, как его отдаёт <c>HostString.Host</c>.</param>
	public static string After(string bind, string port, string? requestHost)
	{
		var host = bind is "0.0.0.0" or "::"
			? string.IsNullOrWhiteSpace(requestHost) ? "127.0.0.1" : requestHost
			: bind.Contains(':', StringComparison.Ordinal) && !bind.StartsWith('[') ? $"[{bind}]" : bind;
		return $"http://{host}:{port}/";
	}
}
