using Panel.Maintenance.Domain;

namespace Panel.Web.Application;

/// <summary>
/// Русская подпись назначения аренды для строки операций (интерфейс 2.1 §1). Текст интерфейса,
/// поэтому лежит здесь, а не в домене Maintenance.
/// </summary>
public static class LeasePurposeTitles
{
	public static string ToTitle(LeasePurpose purpose) => purpose switch
	{
		LeasePurpose.ScheduledMaintenance => "Плановое обслуживание",
		LeasePurpose.WatchdogRecovery => "Восстановление наблюдателем",
		LeasePurpose.Update => "Обновление",
		LeasePurpose.Provision => "Создание сервера",
		_ => "Обслуживание",
	};
}
