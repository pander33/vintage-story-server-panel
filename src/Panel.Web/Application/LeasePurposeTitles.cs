using Panel.Maintenance.Domain;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Подпись назначения аренды для строки операций (интерфейс 2.1 §1). Текст интерфейса,
/// поэтому лежит здесь, а не в домене Maintenance.
/// </summary>
public static class LeasePurposeTitles
{
	public static Phrase ToTitle(LeasePurpose purpose) => purpose switch
	{
		LeasePurpose.ScheduledMaintenance => Phrase.From("Плановое обслуживание"),
		LeasePurpose.WatchdogRecovery => Phrase.From("Восстановление наблюдателем"),
		LeasePurpose.Update => Phrase.From("Обновление"),
		LeasePurpose.Provision => Phrase.From("Создание сервера"),
		_ => Phrase.From("Обслуживание"),
	};
}
