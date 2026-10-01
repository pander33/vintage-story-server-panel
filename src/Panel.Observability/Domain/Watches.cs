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
/// замер после старта панели ничего не сообщает. Нехватка места и устаревшая копия — тоже
/// при наступлении, а пока держатся, — напоминанием раз в <see cref="DiskReminder"/> и
/// <see cref="BackupReminder"/>: замер идёт каждую минуту, и событие на каждый засоряло ленту
/// и диагностику, когда копий просто ещё не делали.
/// </remarks>
public sealed class ServerWatch
{
	/// <summary>Как часто напоминать, что места всё ещё мало.</summary>
	public static readonly TimeSpan DiskReminder = TimeSpan.FromHours(1);

	/// <summary>Как часто напоминать, что свежей копии всё ещё нет.</summary>
	public static readonly TimeSpan BackupReminder = TimeSpan.FromDays(1);

	private static readonly TimeSpan backupFreshness = TimeSpan.FromDays(3);

	private bool? previousActive;
	private DateTimeOffset? diskReported;
	private DateTimeOffset? backupReported;

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

		if (Due(TelemetryNumbers.Truthy(metrics["disk_warning"]), ref diskReported, DiskReminder, now))
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
		var stale = backup is null
					|| now.ToUnixTimeSeconds() - (long) TelemetryNumbers.Number(backup["modified"]) > (long) backupFreshness.TotalSeconds;
		if (Due(stale, ref backupReported, BackupReminder, now))
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

	/// <summary>
	/// Сообщить ли о состоянии: при наступлении и затем раз в <paramref name="reminder"/>.
	/// Прошедшее состояние забывается, и следующее наступление сообщается сразу.
	/// </summary>
	private static bool Due(bool holds, ref DateTimeOffset? reported, TimeSpan reminder, DateTimeOffset now)
	{
		if (!holds)
		{
			reported = null;
			return false;
		}

		if (reported is { } last && now - last < reminder)
		{
			return false;
		}

		reported = now;
		return true;
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
