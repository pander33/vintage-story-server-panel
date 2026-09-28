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

	public required Phrase Group { get; init; }

	public required Phrase Label { get; init; }

	public required Phrase Hint { get; init; }

	/// <summary>Текст подтверждения; если его нет, подтверждение не требуется.</summary>
	public Phrase? ConfirmPrompt { get; init; }

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
			Group = Phrase.From("Диагностика"), Label = Phrase.From("Статистика сервера"),
			Hint = Phrase.From("/stats — тики, память, загрузка"),
		},
		new()
		{
			Action = "list-clients", Template = "/list clients", ReadsBack = true,
			Group = Phrase.From("Диагностика"), Label = Phrase.From("Список подключённых"), Hint = Phrase.From("/list clients"),
		},
		new()
		{
			Action = "list-banned", Template = "/list banned", ReadsBack = true,
			Group = Phrase.From("Диагностика"), Label = Phrase.From("Список заблокированных"), Hint = Phrase.From("/list banned"),
		},
		new()
		{
			Action = "time-show", Template = "/time", ReadsBack = true,
			Group = Phrase.From("Диагностика"), Label = Phrase.From("Текущее игровое время"), Hint = Phrase.From("/time"),
		},
		new()
		{
			Action = "autosave-now", Template = "/autosavenow", ReadsBack = true,
			Group = Phrase.From("Обслуживание"), Label = Phrase.From("Сохранить мир сейчас"),

            // /autosavenow одним словом: «/autosave now» сервер не распознает.
            Hint = Phrase.From("/autosavenow"),
		},
		new()
		{
			Action = "genbackup", Template = "/genbackup", ReadsBack = true,
			Group = Phrase.From("Обслуживание"), Label = Phrase.From("Игровая резервная копия"),
			Hint = Phrase.From("/genbackup — копию делает сама игра, без остановки сервера"),
			ConfirmPrompt = Phrase.From("Запустить игровое резервное копирование? Это нагрузит сервер на время операции."),
		},
		new()
		{
			Action = "time-stop", Template = "/time stop",
			Group = Phrase.From("Время"), Label = Phrase.From("Остановить время"), Hint = Phrase.From("/time stop"),
			ConfirmPrompt = Phrase.From("Остановить течение игрового времени на сервере?"),
		},
		new()
		{
			Action = "time-resume", Template = "/time resume",
			Group = Phrase.From("Время"), Label = Phrase.From("Возобновить время"), Hint = Phrase.From("/time resume"),
		},
		new()
		{
			Action = "time-set", Template = "/time set {value}",
			Group = Phrase.From("Время"), Label = Phrase.From("Установить время"),

            // Привилегированная сторона всегда принимала и час, и пресет; теперь это описано в
            // одном месте и для интерфейса.
            Hint = Phrase.From("/time set — момент суток или час от 0 до 23.99"),
			Value = new ValueDomain.TimeOfDay(TimePresets, Phrase.From("Момент суток")),
		},
		new()
		{
			Action = "time-speed", Template = "/time speed {value}",
			Group = Phrase.From("Время"), Label = Phrase.From("Скорость времени"), Hint = Phrase.From("/time speed 0–9999"),
			Value = new ValueDomain.Number(0, 9999, 1, 0, Phrase.From("Скорость")) { Default = "60" },
		},
		new()
		{
			Action = "weather-stoprain", Template = "/weather stoprain",
			Group = Phrase.From("Погода"), Label = Phrase.From("Остановить дождь"), Hint = Phrase.From("/weather stoprain"),
		},
		new()
		{
			Action = "weather-auto", Template = "/weather setprecipa",
			Group = Phrase.From("Погода"), Label = Phrase.From("Вернуть автоматическую погоду"), Hint = Phrase.From("/weather setprecipa"),
		},
		new()
		{
			Action = "weather-precip", Template = "/weather setprecip {value}",
			Group = Phrase.From("Погода"), Label = Phrase.From("Задать осадки"), Hint = Phrase.From("/weather setprecip от -1 до 1"),
			Value = new ValueDomain.Number(-1, 1, 0.1m, 3, Phrase.From("Интенсивность")) { Default = "0" },
		},
		new()
		{
			Action = "weather-wind", Template = "/weather setw {value}",
			Group = Phrase.From("Погода"), Label = Phrase.From("Задать ветер"), Hint = Phrase.From("/weather setw"),
			Value = new ValueDomain.Choice(WindPresets, Phrase.From("Сила ветра")),
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
