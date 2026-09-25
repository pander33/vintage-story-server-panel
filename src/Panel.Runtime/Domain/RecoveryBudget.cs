using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.SharedKernel;

namespace Panel.Runtime.Domain;

/// <summary>
/// Можно ли наблюдателю перезапустить зависший сервер сейчас.
/// </summary>
/// <remarks>
/// Право на перезапуск даёт только вердикт <see cref="HealthState.Hung"/>
/// (<see cref="ReadinessVerdict.AuthorizesAutomaticRecovery"/>); бюджет лишь ограничивает
/// частоту: не больше <c>max_restarts_hour</c> в час и не чаще паузы. Иначе сервер, который
/// зависает сразу после старта, перезапускался бы бесконечно.
/// </remarks>
public static class RecoveryBudget
{
	private static readonly TimeSpan window = TimeSpan.FromHours(1);

	/// <summary>Перезапуски за последний час; более старые не учитываются.</summary>
	public static IReadOnlyList<DateTimeOffset> Recent(IEnumerable<DateTimeOffset> restarts, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(restarts);
		return [.. restarts.Where(moment => moment >= now - window).Order()];
	}

	/// <summary><c>null</c>, если можно; иначе причина запрета для поля <c>recovery_blocked</c>.</summary>
	public static string? Blocker(IReadOnlyList<DateTimeOffset> recent, WatchdogSettings settings, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(recent);
		ArgumentNullException.ThrowIfNull(settings);

		if (recent.Count >= settings.MaxRestartsPerHour)
		{
			return "Достигнут часовой лимит автоперезапусков";
		}

		return recent.Count > 0 && now - recent[^1] < settings.Cooldown
			? "Действует пауза между автоперезапусками"
			: null;
	}
}

/// <summary>
/// Настройки наблюдателя в <c>automation.json</c> и в теле <c>PUT /api/automation/watchdog</c>.
/// </summary>
public static class WatchdogSettingsJson
{
	/// <summary>
	/// Умолчания поверх присланного, целые числа, проверка границ.
	/// </summary>
	public static Result<WatchdogSettings> FromJson(JsonObject? values)
	{
		var defaults = WatchdogSettings.Default;
		var settings = defaults with
		{
			Enabled = Flag(values?["enabled"], defaults.Enabled),
			AutoRestart = Flag(values?["auto_restart"], defaults.AutoRestart),
			StartupTimeout = TimeSpan.FromSeconds(Integer(values?["startup_timeout"], (int) defaults.StartupTimeout.TotalSeconds)),
			StaleThreshold = TimeSpan.FromSeconds(Integer(values?["stale_seconds"], (int) defaults.StaleThreshold.TotalSeconds)),
			FailureThreshold = Integer(values?["failure_threshold"], defaults.FailureThreshold),
			Cooldown = TimeSpan.FromSeconds(Integer(values?["cooldown_seconds"], (int) defaults.Cooldown.TotalSeconds)),
			MaxRestartsPerHour = Integer(values?["max_restarts_hour"], defaults.MaxRestartsPerHour),
		};

		return settings.Validate();
	}

	public static JsonObject ToJson(WatchdogSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);

		return new JsonObject
		{
			["enabled"] = settings.Enabled,
			["auto_restart"] = settings.AutoRestart,
			["startup_timeout"] = (int) settings.StartupTimeout.TotalSeconds,
			["stale_seconds"] = (int) settings.StaleThreshold.TotalSeconds,
			["failure_threshold"] = settings.FailureThreshold,
			["cooldown_seconds"] = (int) settings.Cooldown.TotalSeconds,
			["max_restarts_hour"] = settings.MaxRestartsPerHour,
		};
	}

	private static bool Flag(JsonNode? node, bool fallback) => node switch
	{
		JsonValue value when value.GetValueKind() == JsonValueKind.True => true,
		JsonValue value when value.GetValueKind() == JsonValueKind.False => false,
		_ => fallback,
	};

	private static int Integer(JsonNode? node, int fallback) =>
		node is JsonValue value
		&& double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
		&& double.IsFinite(number)
			? (int) number
			: fallback;
}
