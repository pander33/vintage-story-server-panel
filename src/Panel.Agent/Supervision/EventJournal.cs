using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.Versioning;

namespace Panel.Agent.Supervision;

/// <summary>
/// Журнал служб панели на Windows: записи журнала событий «Приложение» от источников панели
/// и демона. Замена <c>journalctl --unit vs-panel</c>.
/// </summary>
/// <remarks>
/// Строки в формате <c>short-iso</c>, как у journalctl: <c>время узел источник[pid]: текст</c>,
/// по одной на каждую строку записи. Заголовок записи логгера .NET (<c>Category: …</c> и
/// <c>EventId: …</c>) сводится к префиксу первой строки.
/// </remarks>
public static class EventJournal
{
	/// <summary>Источник веб-процесса: имя приложения, которое задаёт <c>UseWindowsService</c>.</summary>
	public const string PanelSource = "Panel.Web";

	/// <summary>Источник демона (<c>AgentWindowsService</c>).</summary>
	public const string AgentSource = "vs-panel-agent";

	/// <summary>Источник службы панели: запуск и остановку пишет сам <c>ServiceBase</c>.</summary>
	public const string ServiceSource = "vs-panel";

	/// <summary>
	/// Последние <paramref name="count"/> строк, старые первыми. Журнал событий общий и бывает
	/// большим, поэтому чтение идёт с конца и останавливается, набрав нужное.
	/// </summary>
	[SupportedOSPlatform("windows")]
	public static IReadOnlyList<string> Tail(int count)
	{
		var query = new EventLogQuery(
			"Application",
			PathType.LogName,
			$"*[System[Provider[@Name='{PanelSource}' or @Name='{ServiceSource}' or @Name='{AgentSource}']]]")
		{
			ReverseDirection = true,
		};

		var newestFirst = new List<IReadOnlyList<string>>();
		var total = 0;
		using var reader = new EventLogReader(query);
		while (total < count && reader.ReadEvent() is { } record)
		{
			using (record)
			{
				var lines = Format(
					record.TimeCreated ?? DateTime.Now,
					record.MachineName,
					record.ProviderName,
					record.ProcessId ?? 0,
					Level(record.Level),
					Description(record));
				newestFirst.Add(lines);
				total += lines.Count;
			}
		}

		newestFirst.Reverse();
		var all = newestFirst.SelectMany(lines => lines).ToList();
		return all.Count > count ? all[^count..] : all;
	}

	/// <summary>Одна запись журнала событий строками <c>short-iso</c>.</summary>
	public static IReadOnlyList<string> Format(
		DateTime time, string machine, string source, int pid, string level, string description)
	{
		ArgumentNullException.ThrowIfNull(description);

		var host = machine.Split('.')[0];
		var stamp = time.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
			+ time.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", string.Empty, StringComparison.Ordinal);
		var prefix = pid > 0
			? string.Create(CultureInfo.InvariantCulture, $"{stamp} {host} {source}[{pid}]: ")
			: $"{stamp} {host} {source}: ";

		string? category = null;
		var body = new List<string>();
		foreach (var raw in description.Split('\n'))
		{
			var line = raw.TrimEnd('\r');
			if (body.Count == 0 && category is null && line.StartsWith("Category: ", StringComparison.Ordinal))
			{
				category = line["Category: ".Length..];
				continue;
			}

			if (body.Count == 0 && line.StartsWith("EventId: ", StringComparison.Ordinal))
			{
				continue;
			}

			if (line.Trim().Length > 0)
			{
				body.Add(line);
			}
		}

		if (body.Count == 0)
		{
			body.Add(string.Empty);
		}

		var head = level + ": " + (category is null ? string.Empty : category + ": ");
		return [prefix + head + body[0], .. body.Skip(1).Select(line => prefix + line)];
	}

	private static string Level(byte? level) => level switch
	{
		1 => "crit",
		2 => "fail",
		3 => "warn",
		_ => "info",
	};

	/// <summary>
	/// Текст записи. Без файла сообщений источника Windows его не соберёт, тогда вставки
	/// берутся как есть.
	/// </summary>
	[SupportedOSPlatform("windows")]
	private static string Description(EventRecord record)
	{
		try
		{
			if (record.FormatDescription() is { Length: > 0 } text)
			{
				return text;
			}
		}
		catch (EventLogException)
		{
		}

		return string.Join(' ', record.Properties.Select(property => Convert.ToString(property.Value, CultureInfo.InvariantCulture)));
	}
}
