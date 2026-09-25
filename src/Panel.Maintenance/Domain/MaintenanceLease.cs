using System.Text.Json.Nodes;
using Panel.SharedKernel;

namespace Panel.Maintenance.Domain;

/// <summary>Зачем взята аренда. Аренда одна на панель, для любой операции.</summary>
public enum LeasePurpose
{
	ScheduledMaintenance,
	WatchdogRecovery,
	Update,

	/// <summary>Создание сервера.</summary>
	Provision,
}

public static class LeasePurposeWire
{
	public static string ToWire(LeasePurpose purpose) => purpose switch
	{
		LeasePurpose.ScheduledMaintenance => "maintenance",
		LeasePurpose.WatchdogRecovery => "watchdog-recovery",
		LeasePurpose.Provision => "provision",
		_ => "update",
	};

	public static LeasePurpose? FromWire(string? text) => text switch
	{
		"maintenance" => LeasePurpose.ScheduledMaintenance,
		"watchdog-recovery" => LeasePurpose.WatchdogRecovery,
		"update" => LeasePurpose.Update,
		"provision" => LeasePurpose.Provision,
		_ => null,
	};
}

/// <summary>
/// Процесс панели, который держит аренду. Pid переиспользуется, поэтому сравнивается тройка:
/// загрузка, pid и момент запуска процесса в тактах от загрузки.
/// </summary>
/// <remarks>
/// Идея та же, что у <c>ProcessIdentity</c> контекста Runtime, но процесс другой: там игра,
/// здесь сама панель (детализация Maintenance §4).
/// </remarks>
public readonly record struct LeaseHolder(string Boot, int Pid, long StartTicks)
{
	public string Serialize() => $"{Boot}:{Pid}:{StartTicks}";

	public static LeaseHolder? Parse(string? text)
	{
		var parts = (text ?? string.Empty).Split(':');
		return parts.Length == 3
			   && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var pid)
			   && long.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var ticks)
			? new LeaseHolder(parts[0], pid, ticks)
			: null;
	}
}

/// <summary>
/// Аренда обслуживания на диске. Пока файл существует, панель занята.
/// </summary>
/// <remarks>
/// <para>
/// Детализация Maintenance §4. Аренда переживает процесс: после аварии панели при старте
/// видно, чья она и жив ли владелец, и понятно, что операция оборвалась.
/// </para>
/// <para>
/// Срока у аренды нет, в отличие от §4. Операция длится до получаса (копия офлайн,
/// распаковка пакета), и продление по таймеру добавило бы способ потерять аренду посреди
/// работы. Аренда считается брошенной, когда её владелец — не текущий процесс и такого
/// процесса больше нет.
/// </para>
/// </remarks>
public sealed record MaintenanceLease(
	Guid Id,
	LeasePurpose Purpose,
	InstanceId Instance,
	LeaseHolder Holder,
	long AcquiredAt)
{
	public static MaintenanceLease Acquire(LeasePurpose purpose, InstanceId instance, LeaseHolder holder, long now) =>
		new(Guid.NewGuid(), purpose, instance, holder, now);

	/// <summary>Брошена: её держит не этот процесс, и владельца больше нет.</summary>
	public bool IsAbandoned(LeaseHolder current, bool holderAlive) => Holder != current && !holderAlive;

	public JsonObject ToJson() => new()
	{
		["version"] = 1,
		["id"] = Id.ToString("N"),
		["purpose"] = LeasePurposeWire.ToWire(Purpose),
		["instance"] = Instance.Value,
		["holder"] = Holder.Serialize(),
		["acquired_at"] = AcquiredAt,
	};

	/// <summary>Разбор файла аренды; нечитаемый файл даёт <c>null</c>, и такая аренда считается брошенной.</summary>
	public static MaintenanceLease? FromJson(JsonNode? node)
	{
		if (node is not JsonObject root
			|| !Guid.TryParseExact(Text(root["id"]), "N", out var id)
			|| LeasePurposeWire.FromWire(Text(root["purpose"])) is not { } purpose
			|| LeaseHolder.Parse(Text(root["holder"])) is not { } holder)
		{
			return null;
		}

		var instance = InstanceId.Create(Text(root["instance"]));
		if (!instance.IsSuccess)
		{
			return null;
		}

		var acquired = OperationRecovery.Seconds(root["acquired_at"]) ?? 0;
		return new MaintenanceLease(id, purpose, instance.Value, holder, acquired);
	}

	private static string? Text(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
