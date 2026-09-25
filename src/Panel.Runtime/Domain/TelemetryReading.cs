namespace Panel.Runtime.Domain;

/// <summary>
/// Результат чтения телеметрии. Закрытая иерархия вместо словаря с флагами available / stale /
/// age_seconds / detail, в котором можно записать «недоступна, но вот снимок».
/// </summary>
public abstract record TelemetryReading
{
	private TelemetryReading()
	{
	}

	/// <summary>Свежий снимок: мод пишет, файл моложе порога устаревания.</summary>
	public sealed record Fresh(TelemetrySnapshot Snapshot) : TelemetryReading;

	/// <summary>Снимок есть, но старше порога. Возраст относится к чтению, а не к снимку.</summary>
	public sealed record Stale(TelemetrySnapshot Snapshot, TimeSpan Age) : TelemetryReading;

	/// <summary>Снимка нет. Причина по-русски, она показывается в интерфейсе.</summary>
	public sealed record Unavailable(string Detail) : TelemetryReading;
}
