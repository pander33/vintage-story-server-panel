using System.Globalization;
using System.Text.Json.Nodes;
using Panel.SharedKernel;

namespace Panel.Observability.Domain;

/// <summary>
/// События по замеру метрик: служба остановилась или вернулась, заканчивается место, давно не
/// было копии.
/// </summary>
/// <remarks>
/// Переход «работает → остановлен» сообщается только при смене состояния, поэтому первый
/// замер после старта панели ничего не сообщает. Место и копия проверяются при каждом замере:
/// повторные уведомления гасит пауза, а в ленту событий они попадают каждый раз.
/// </remarks>
public sealed class ServerWatch
{
	private static readonly TimeSpan backupFreshness = TimeSpan.FromDays(3);

	private bool? previousActive;

	public IReadOnlyList<PanelEvent> Observe(JsonObject metrics, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(metrics);

		var events = new List<PanelEvent>();
		var active = TelemetryNumbers.Truthy(metrics["active"]);

		if (previousActive is { } previous && previous != active)
		{
			events.Add(Event(
				active ? "server.recovered" : "server.stopped",
				active ? EventSeverity.Info : EventSeverity.Error,
				active ? Phrase.From("Сервер снова работает") : Phrase.From("Сервер остановлен"),
				null,
				now));
		}

		previousActive = active;

		if (TelemetryNumbers.Truthy(metrics["disk_warning"]))
		{
			var freeGigabytes = (long) TelemetryNumbers.Number(metrics["disk_free"]) / (1024L * 1024 * 1024);
			events.Add(Event(
				"disk.low",
				EventSeverity.Critical,
				Phrase.From("Заканчивается место на диске"),
				Phrase.Format($"Свободно {freeGigabytes} ГБ"),
				now));
		}

		var backup = metrics["last_backup"] as JsonObject;
		if (backup is null
			|| now.ToUnixTimeSeconds() - (long) TelemetryNumbers.Number(backup["modified"]) > (long) backupFreshness.TotalSeconds)
		{
			events.Add(Event(
				"backup.stale",
				EventSeverity.Warning,
				Phrase.From("Нет свежей резервной копии"),
				Phrase.From("Последняя копия старше трёх дней или отсутствует."),
				now));
		}

		return events;
	}

	private static PanelEvent Event(string kind, EventSeverity severity, Phrase title, Phrase? detail, DateTimeOffset now) =>
		PanelEvent.Create(kind, severity, title, detail, "monitor", now).Value;
}

/// <summary>
/// События об игроках и тиках по снимку телеметрии.
/// </summary>
/// <remarks>
/// Если телеметрия молчит, состояние сбрасывается, а не все игроки объявляются вышедшими:
/// молчанием сервера или мода занимается наблюдатель готовности. Критический пинг
/// сообщается только после пяти плохих снимков подряд, одиночный скачок нормален.
/// </remarks>
public sealed class TelemetryWatch
{
	private const int pingCriticalSamples = 5;
	private const int longTicksAlert = 3;

	private readonly Dictionary<string, int> badPing = new(StringComparer.Ordinal);
	private HashSet<string>? known;

	public IReadOnlyList<PanelEvent> Observe(JsonObject snapshot, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		if (!TelemetryNumbers.Truthy(snapshot["available"]) || TelemetryNumbers.Truthy(snapshot["stale"]))
		{
			known = null;
			badPing.Clear();
			return [];
		}

		var events = new List<PanelEvent>();
		var players = (snapshot["players"] as JsonArray ?? []).OfType<JsonObject>().ToList();
		var names = players
			.Select(player => TelemetryNumbers.Text(player["name"]))
			.Where(name => name.Length > 0)
			.ToHashSet(StringComparer.Ordinal);

		if (known is null)
		{
			known = names;
		}
		else
		{
			foreach (var name in names.Except(known).Order(StringComparer.Ordinal))
			{
				events.Add(Event("player.joined", EventSeverity.Info, Phrase.From("Игрок вошёл"), Phrase.From(name), now));
			}

			foreach (var name in known.Except(names).Order(StringComparer.Ordinal))
			{
				events.Add(Event("player.left", EventSeverity.Info, Phrase.From("Игрок вышел"), Phrase.From(name), now));
			}

			known = names;
		}

		foreach (var player in players)
		{
			var name = TelemetryNumbers.Text(player["name"]);
			if (name.Length == 0)
			{
				continue;
			}

			var ping = player["ping_ms"];
			var value = TelemetryNumbers.Number(ping);
			if (ping is not null && ping.GetValueKind() != System.Text.Json.JsonValueKind.Null
				&& value >= TelemetryHistory.PingCriticalMilliseconds)
			{
				badPing[name] = badPing.GetValueOrDefault(name) + 1;
				if (badPing[name] == pingCriticalSamples)
				{
					events.Add(Event(
						"player.ping_critical",
						EventSeverity.Warning,
						Phrase.From("Критический ping игрока"),
						Phrase.Format($"{name}: {Whole(value)} мс"),
						now,
						$"player.ping_critical:{name}"));
				}
			}
			else
			{
				badPing.Remove(name);
			}
		}

		foreach (var name in badPing.Keys.Where(name => !names.Contains(name)).ToList())
		{
			badPing.Remove(name);
		}

		var longTicks = (int) TelemetryNumbers.Number(snapshot["long_tick_count"]);
		if (longTicks >= longTicksAlert)
		{
			var threshold = TelemetryNumbers.Number(snapshot["long_tick_threshold_ms"]);
			events.Add(Event(
				"server.long_tick",
				EventSeverity.Warning,
				Phrase.From("Сервер не успевает обрабатывать тики"),
				Phrase.Format($"{longTicks} длинных тиков подряд, порог {Whole(threshold)} мс"),
				now,
				"server.long_tick"));
		}

		return events;
	}

	/// <summary>Целое число с округлением к чётному.</summary>
	private static string Whole(double value) =>
		Math.Round(value, MidpointRounding.ToEven).ToString("0", CultureInfo.InvariantCulture);

	private static PanelEvent Event(
		string kind,
		EventSeverity severity,
		Phrase title,
		Phrase detail,
		DateTimeOffset now,
		string? dedupeKey = null) =>
		PanelEvent.Create(kind, severity, title, detail, "telemetry", now, dedupeKey).Value;
}
