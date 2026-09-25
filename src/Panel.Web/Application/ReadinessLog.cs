using System.Collections.Concurrent;
using Panel.Runtime.Domain;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Последний вывод наблюдателя по каждому серверу: вердикт, причина, по которой
/// автоперезапуск не выполнен, и настройки наблюдения сервера.
/// </summary>
/// <remarks>
/// <para>
/// Пишет сюда только <see cref="AutomationService"/>: его такт ведёт цепочку «сколько проверок
/// подряд сервер не отвечает» и сохраняет её в <c>automation.json</c>, чтобы после перезапуска
/// панели счёт продолжился. Остальные только читают. Если бы вердикт записывал и
/// <c>/api/status</c>, каждое обновление страницы считалось бы ещё одной неудачей, и сервер
/// «зависал» бы раньше порога.
/// </para>
/// <para>
/// Отдельный объект, а не свойство сервиса автоматизации: сервису нужен
/// <see cref="ServerControlHandler"/> для перезапуска, а обработчику — настройки наблюдения, и
/// через сервис получилась бы циклическая зависимость.
/// </para>
/// </remarks>
public sealed class ReadinessLog
{
	private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

	/// <summary>Последний вердикт такта; <c>null</c>, если проверки ещё не было или сервер только что перезапущен.</summary>
	public ReadinessVerdict? Previous(InstanceId instance) =>
		entries.TryGetValue(instance.Value, out var entry) ? entry.Verdict : null;

	/// <summary>Почему последний автоперезапуск не выполнен: поле <c>recovery_blocked</c>.</summary>
	public string? RecoveryBlocked(InstanceId instance) =>
		entries.TryGetValue(instance.Value, out var entry) ? entry.RecoveryBlocked : null;

	public WatchdogSettings Settings(InstanceId instance) =>
		entries.TryGetValue(instance.Value, out var entry) ? entry.Settings : WatchdogSettings.Default;

	public void Record(InstanceId instance, ReadinessVerdict? verdict, string? recoveryBlocked) =>
		entries.AddOrUpdate(
			instance.Value,
			_ => new Entry(verdict, recoveryBlocked, WatchdogSettings.Default),
			(_, entry) => entry with { Verdict = verdict, RecoveryBlocked = recoveryBlocked });

	public void Configure(InstanceId instance, WatchdogSettings settings) =>
		entries.AddOrUpdate(
			instance.Value,
			_ => new Entry(null, null, settings),
			(_, entry) => entry with { Settings = settings });

	private sealed record Entry(ReadinessVerdict? Verdict, string? RecoveryBlocked, WatchdogSettings Settings);
}
