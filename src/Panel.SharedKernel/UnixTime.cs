namespace Panel.SharedKernel;

/// <summary>
/// Время в файлах состояния — секунды с эпохи, иногда дробные. Формат заморожен (ADR-04),
/// поэтому преобразование собрано в одном месте.
/// </summary>
public static class UnixTime
{
	public static DateTimeOffset FromSeconds(double seconds) =>
		DateTimeOffset.FromUnixTimeMilliseconds((long) Math.Round(seconds * 1000));

	public static double ToSeconds(DateTimeOffset moment) =>
		moment.ToUnixTimeMilliseconds() / 1000d;

	public static DateTimeOffset? FromSecondsOrNull(double? seconds) =>
		seconds is null or 0 ? null : FromSeconds(seconds.Value);
}
