using System.Text.Json.Serialization;
using Panel.SharedKernel;

namespace Panel.Contracts.ServerCommands;

/// <summary>
/// Одно разрешённое консольное действие: что показать в интерфейсе и во что оно
/// разворачивается на сервере.
/// </summary>
/// <remarks>
/// <see cref="Template"/> содержит не больше одной подстановки, <c>{value}</c>. Это проверяет
/// контрактный тест: другая подстановка позволила бы собрать произвольную команду.
/// </remarks>
public sealed record ServerCommandSpec
{
	public required string Action { get; init; }

	/// <summary>Например <c>/time set {value}</c>.</summary>
	[JsonIgnore]
	public required string Template { get; init; }

	/// <summary>Читать ли ответ обратно из журнала.</summary>
	public bool ReadsBack { get; init; }

	[JsonIgnore]
	public ValueDomain? Value { get; init; }

	public required string Group { get; init; }

	public required string Label { get; init; }

	public required string Hint { get; init; }

	/// <summary>Текст подтверждения; если его нет, подтверждение не требуется.</summary>
	public string? ConfirmPrompt { get; init; }

	/// <summary>
	/// Развернуть шаблон. Значение проверяется областью значений: метод вызывается и в
	/// веб-процессе, и на привилегированной стороне.
	/// </summary>
	public Result<string> Render(string? raw)
	{
		if (Value is null)
		{
			return string.IsNullOrWhiteSpace(raw)
				? Template
				: Failure.Invalid("Это действие не принимает значения");
		}

		return Value.Validate(raw).Map(value => Template.Replace("{value}", value, StringComparison.Ordinal));
	}
}

/// <summary>
/// Каталог консольных действий, один на всю панель: по нему рисует интерфейс и проверяет
/// привилегированная сторона.
/// </summary>
public static class ServerCommandCatalogue
{
	public static IReadOnlyList<string> TimePresets { get; } =
	[
		"sunrise", "morning", "latemorning", "lunch", "afternoon", "sunset",
		"night", "latenight", "midnight", "witchinghour", "day",
	];

	public static IReadOnlyList<string> WindPresets { get; } =
		["still", "lightbreeze", "mediumbreeze", "strongbreeze", "storm"];

	public static IReadOnlyList<string> GameModes { get; } =
		["guest", "survival", "creative", "spectator"];

	public static IReadOnlyList<ServerCommandSpec> All { get; } =
	[
		new()
		{
			Action = "stats", Template = "/stats", ReadsBack = true,
			Group = "Диагностика", Label = "Статистика сервера",
			Hint = "/stats — тики, память, загрузка",
		},
		new()
		{
			Action = "list-clients", Template = "/list clients", ReadsBack = true,
			Group = "Диагностика", Label = "Список подключённых", Hint = "/list clients",
		},
		new()
		{
			Action = "list-banned", Template = "/list banned", ReadsBack = true,
			Group = "Диагностика", Label = "Список заблокированных", Hint = "/list banned",
		},
		new()
		{
			Action = "time-show", Template = "/time", ReadsBack = true,
			Group = "Диагностика", Label = "Текущее игровое время", Hint = "/time",
		},
		new()
		{
			Action = "autosave-now", Template = "/autosavenow", ReadsBack = true,
			Group = "Обслуживание", Label = "Сохранить мир сейчас",

            // /autosavenow одним словом: «/autosave now» сервер не распознает.
            Hint = "/autosavenow",
		},
		new()
		{
			Action = "genbackup", Template = "/genbackup", ReadsBack = true,
			Group = "Обслуживание", Label = "Игровая резервная копия",
			Hint = "/genbackup — копию делает сама игра, без остановки сервера",
			ConfirmPrompt = "Запустить игровое резервное копирование? Это нагрузит сервер на время операции.",
		},
		new()
		{
			Action = "time-stop", Template = "/time stop",
			Group = "Время", Label = "Остановить время", Hint = "/time stop",
			ConfirmPrompt = "Остановить течение игрового времени на сервере?",
		},
		new()
		{
			Action = "time-resume", Template = "/time resume",
			Group = "Время", Label = "Возобновить время", Hint = "/time resume",
		},
		new()
		{
			Action = "time-set", Template = "/time set {value}",
			Group = "Время", Label = "Установить время",

            // Привилегированная сторона всегда принимала и час, и пресет; теперь это описано в
            // одном месте и для интерфейса.
            Hint = "/time set — момент суток или час от 0 до 23.99",
			Value = new ValueDomain.TimeOfDay(TimePresets, "Момент суток"),
		},
		new()
		{
			Action = "time-speed", Template = "/time speed {value}",
			Group = "Время", Label = "Скорость времени", Hint = "/time speed 0–9999",
			Value = new ValueDomain.Number(0, 9999, 1, 0, "Скорость") { Default = "60" },
		},
		new()
		{
			Action = "weather-stoprain", Template = "/weather stoprain",
			Group = "Погода", Label = "Остановить дождь", Hint = "/weather stoprain",
		},
		new()
		{
			Action = "weather-auto", Template = "/weather setprecipa",
			Group = "Погода", Label = "Вернуть автоматическую погоду", Hint = "/weather setprecipa",
		},
		new()
		{
			Action = "weather-precip", Template = "/weather setprecip {value}",
			Group = "Погода", Label = "Задать осадки", Hint = "/weather setprecip от -1 до 1",
			Value = new ValueDomain.Number(-1, 1, 0.1m, 3, "Интенсивность") { Default = "0" },
		},
		new()
		{
			Action = "weather-wind", Template = "/weather setw {value}",
			Group = "Погода", Label = "Задать ветер", Hint = "/weather setw",
			Value = new ValueDomain.Choice(WindPresets, "Сила ветра"),
		},
	];

	private static readonly Dictionary<string, ServerCommandSpec> byAction =
		All.ToDictionary(spec => spec.Action, StringComparer.Ordinal);

	public static Result<ServerCommandSpec> Find(string? action) =>
		action is not null && byAction.TryGetValue(action, out var spec)
			? spec
			: Failure.Invalid("Неизвестное действие сервера");
}

/// <summary>
/// Запрос на выполнение действия: что и с каким значением.
/// </summary>
/// <remarks>
/// Отдельный тип вместо двух строк в сигнатуре, чтобы их нельзя было перепутать местами.
/// Значения не проверены: это данные от клиента.
/// </remarks>
public sealed record ServerCommandRequest(string Action, string Value = "");
