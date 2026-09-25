using System.Text.RegularExpressions;

namespace Panel.Observability.Domain;

/// <summary>Объяснение для оператора: что случилось и что делать.</summary>
public sealed record Finding(
	string Level,
	string Code,
	string Title,
	string Detail,
	string? Evidence = null,
	long? Timestamp = null);

/// <summary>Итог разбора: причина состояния запуска и последние отказы в подключении.</summary>
public sealed record Diagnosis(Finding Startup, IReadOnlyList<Finding> Connections);

/// <summary>
/// Объяснения по журналу.
/// </summary>
/// <remarks>
/// <para>
/// Причина состояния запуска ищется только в текущей сессии, после последней строки старта:
/// ошибка прошлого процесса нынешнее состояние не объясняет. Поиск идёт с конца, и выигрывает
/// первое совпавшее правило. Правила упорядочены от частных к общим, «сервер завершился»
/// стоит последним.
/// </para>
/// <para>
/// «Сервер готов» определяет не журнал, а готовность: процесс активен и наблюдатель видит
/// свежую телеметрию. Строка «Dedicated Server now running» готовностью не считается (INV-3).
/// </para>
/// </remarks>
public static partial class LogDiagnosis
{
	private const int evidenceTail = 800;
	private const int maxConnections = 5;

	private static readonly Rule[] startupCauses =
	[
		new("config-permission", PermissionDenied(), "Нет доступа к файлам сервера",
			"Проверьте владельца и права serverconfig.json и каталога data."),
		new("port-busy", PortBusy(), "Порт уже занят",
			"Освободите порт или укажите другой Port в настройках сервера."),
		new("memory", OutOfMemory(), "Недостаточно памяти",
			"Уменьшите нагрузку или лимиты мира и проверьте доступную RAM."),
		new("runtime", RuntimeMissing(), "Не подходит версия .NET",
			"Установите версию .NET, требуемую этой сборкой Vintage Story."),
		new("config-invalid", ConfigInvalid(), "Ошибка конфигурации сервера",
			"Проверьте последнее изменение serverconfig.json или выполните откат."),
		new("mod-startup", ModStartup(), "Ошибка мода при запуске",
			"Отключите недавно изменённый мод и проверьте его совместимость."),
		new("startup-crash", CrashSummary.Crash(), "Сервер завершился при запуске",
			"Откройте журнал ниже: показана последняя найденная критическая строка."),
	];

	private static readonly Rule[] connectionCauses =
	[
		new("whitelist", NotWhitelisted(), "Игрока нет в белом списке",
			"Добавьте игрока в whitelist либо измените WhitelistMode."),
		new("banned", Banned(), "Игрок заблокирован",
			"Проверьте список блокировок и снимите бан при необходимости."),
		new("password", WrongPassword(), "Неверный пароль сервера",
			"Проверьте пароль на клиенте или настройку Password сервера."),
		new("authentication", AuthenticationFailed(), "Не пройдена авторизация Vintage Story",
			"Проверьте учётную запись игрока, VerifyPlayerAuth и доступ сервера к системе авторизации."),
		new("version", VersionMismatch(), "Несовместимая версия клиента",
			"Версия игры и набор модов клиента должны соответствовать серверу."),
		new("full", ServerFull(), "На сервере нет свободных мест",
			"Увеличьте MaxClients или дождитесь свободного места."),
		new("connection-rejected", ConnectionRejected(), "Подключение отклонено",
			"Точная причина приведена в строке журнала."),
	];

	/// <param name="active">Служба активна.</param>
	/// <param name="ready">Наблюдатель подтвердил готовность (состояние <c>ready</c>).</param>
	/// <param name="notReadyDetail">Пояснение наблюдателя, если готовности нет.</param>
	public static Diagnosis Diagnose(
		bool active,
		bool ready,
		WatchdogNote notReadyDetail,
		IReadOnlyList<LogEvent> events)
	{
		ArgumentNullException.ThrowIfNull(events);

		var sessionStart = 0;
		for (var index = 0; index < events.Count; index++)
		{
			if (SessionStart().IsMatch(events[index].Line))
			{
				sessionStart = index;
			}
		}

		return new Diagnosis(
			Startup(active, ready, notReadyDetail, events, sessionStart),
			Connections(events));
	}

	private static Finding Startup(
		bool active,
		bool ready,
		WatchdogNote note,
		IReadOnlyList<LogEvent> events,
		int sessionStart)
	{
		if (active && ready)
		{
			return new Finding("ok", "ready", "Сервер успешно запущен",
				"Игровой процесс работает и передаёт актуальную телеметрию.");
		}

		for (var index = events.Count - 1; index >= sessionStart; index--)
		{
			var line = events[index].Line;
			if (startupCauses.FirstOrDefault(rule => rule.Pattern.IsMatch(line)) is { } rule)
			{
				return new Finding("error", rule.Code, rule.Title, rule.Action, Tail(line), events[index].Timestamp);
			}
		}

		return active
			? new Finding("warn", "not-ready", "Сервер ещё не готов",
				string.IsNullOrEmpty(note.Text) ? "Процесс запущен, но готовность игры не подтверждена." : note.Text)
			: new Finding("warn", "stopped", "Игровая служба остановлена",
				"В доступном фрагменте журнала явная причина не найдена.");
	}

	private static IReadOnlyList<Finding> Connections(IReadOnlyList<LogEvent> events)
	{
		var found = new List<Finding>();
		var seen = new HashSet<(string, string)>();

		for (var index = events.Count - 1; index >= 0 && found.Count < maxConnections; index--)
		{
			var line = events[index].Line;
			if (connectionCauses.FirstOrDefault(rule => rule.Pattern.IsMatch(line)) is not { } rule
				|| !seen.Add((rule.Code, line)))
			{
				continue;
			}

			found.Add(new Finding("error", rule.Code, rule.Title, rule.Action, Tail(line), events[index].Timestamp));
		}

		return found;
	}

	/// <summary>Последние 800 символов: причина обычно в конце строки, в начале метка и узел.</summary>
	private static string Tail(string line) => line.Length > evidenceTail ? line[^evidenceTail..] : line;

	private sealed record Rule(string Code, Regex Pattern, string Title, string Action);

	private const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

	[GeneratedRegex(@"(?:Started .*Vintage Story Server|Server logger started|Launching server\.\.\.)", options)]
	private static partial Regex SessionStart();

	[GeneratedRegex(@"unauthorizedaccessexception|permission denied|access to the path .* is denied", options)]
	private static partial Regex PermissionDenied();

	[GeneratedRegex(@"address already in use|failed to bind|bind.*(?:failed|error)|socket.*in use", options)]
	private static partial Regex PortBusy();

	[GeneratedRegex(@"outofmemory|out of memory|oom-kill|killed process", options)]
	private static partial Regex OutOfMemory();

	[GeneratedRegex(@"hostfxr|compatible framework|framework.*not found|failed to load.*coreclr", options)]
	private static partial Regex RuntimeMissing();

	[GeneratedRegex(@"serverconfig\.json|loadconfig|jsonexception|invalid.*config|default group code", options)]
	private static partial Regex ConfigInvalid();

	[GeneratedRegex(@"(?:mod|modsystem|assembly).*(?:failed|exception|could not|error)", options)]
	private static partial Regex ModStartup();

	[GeneratedRegex(@"(?:not|isn't|is not).{0,40}whitelist|whitelist.{0,40}(?:denied|reject|not allowed)", options)]
	private static partial Regex NotWhitelisted();

	[GeneratedRegex(@"(?:connection|player|client).{0,80}\bbann?ed\b|\bbann?ed\b.{0,80}(?:connect|join)", options)]
	private static partial Regex Banned();

	[GeneratedRegex(@"(?:wrong|invalid|bad).{0,30}password|password.{0,30}(?:wrong|invalid|failed)", options)]
	private static partial Regex WrongPassword();

	[GeneratedRegex(@"(?:auth|authentication|verify player).{0,80}(?:failed|invalid|denied|unavailable)|not authenticated", options)]
	private static partial Regex AuthenticationFailed();

	[GeneratedRegex(@"(?:network|game|client|server).{0,40}version.{0,60}(?:mismatch|incompatible|wrong)|incompatible.{0,30}version", options)]
	private static partial Regex VersionMismatch();

	[GeneratedRegex(@"server.{0,20}(?:is )?full|max(?:imum)? clients|no free slot", options)]
	private static partial Regex ServerFull();

	[GeneratedRegex(@"connection.{0,50}(?:rejected|refused|denied)|disconnecting client.{0,80}(?:because|reason)", options)]
	private static partial Regex ConnectionRejected();
}

/// <summary>
/// Пояснение наблюдателя готовности. Обёртка нужна, потому что доменные методы не принимают
/// голый <c>string</c>.
/// </summary>
public readonly record struct WatchdogNote(string? Text)
{
	public static WatchdogNote None => new(null);
}
