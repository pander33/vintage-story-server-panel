using Panel.Agent.Supervision;

namespace Panel.Agent.Tests;

/// <summary>
/// Журнал службы панели на Windows: запись журнала событий превращается в строки
/// <c>journalctl --output=short-iso</c> для раздела «Журнал службы панели».
/// </summary>
public sealed class EventJournalTests
{
	private static readonly DateTime moment = new(2026, 9, 24, 11, 1, 18, DateTimeKind.Local);

	private static string Stamp =>
		moment.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
		+ moment.ToString("zzz", System.Globalization.CultureInfo.InvariantCulture).Replace(":", string.Empty, StringComparison.Ordinal);

	[Fact]
	public void Заголовок_логгера_становится_префиксом_первой_строки()
	{
		var lines = EventJournal.Format(
			moment,
			"HOST.corp.local",
			EventJournal.PanelSource,
			4242,
			"info",
			"Category: Microsoft.Hosting.Lifetime\r\nEventId: 14\r\n\r\nNow listening on: http://127.0.0.1:8080\r\n");

		Assert.Equal([$"{Stamp} HOST Panel.Web[4242]: info: Microsoft.Hosting.Lifetime: Now listening on: http://127.0.0.1:8080"], lines);
	}

	[Fact]
	public void Многострочная_запись_даёт_строку_на_строку_с_тем_же_префиксом()
	{
		var lines = EventJournal.Format(
			moment,
			"HOST",
			EventJournal.PanelSource,
			7,
			"fail",
			"Category: Panel.Web.Monitoring\r\nEventId: 0\r\n\r\nСбой цикла\r\n\r\nSystem.IO.IOException: занято\r\n   at X.Y()");

		Assert.Equal(
			[
				$"{Stamp} HOST Panel.Web[7]: fail: Panel.Web.Monitoring: Сбой цикла",
				$"{Stamp} HOST Panel.Web[7]: System.IO.IOException: занято",
				$"{Stamp} HOST Panel.Web[7]:    at X.Y()",
			],
			lines);
	}

	[Fact]
	public void Запись_без_заголовка_и_без_pid_тоже_строка()
	{
		var lines = EventJournal.Format(moment, "HOST", EventJournal.AgentSource, 0, "fail", "vs-panel-agent остановился с ошибкой: канал занят");

		Assert.Equal([$"{Stamp} HOST vs-panel-agent: fail: vs-panel-agent остановился с ошибкой: канал занят"], lines);
	}

	[Fact]
	public void Чтение_журнала_событий_не_отказывает()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		Assert.True(EventJournal.Tail(20).Count <= 20);
	}
}
