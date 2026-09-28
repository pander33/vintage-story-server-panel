using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Panel.SharedKernel;

namespace Panel.Configuration.Domain;

/// <summary>Тип значения поля; в протоколе передаётся строчным словом (<see cref="ConfigFieldKindWire"/>).</summary>
public enum ConfigFieldKind
{
	Boolean,
	Integer,
	Number,
	String,
	Select,
}

public static class ConfigFieldKindWire
{
	public static string ToWire(ConfigFieldKind kind) => kind switch
	{
		ConfigFieldKind.Boolean => "boolean",
		ConfigFieldKind.Integer => "integer",
		ConfigFieldKind.Number => "number",
		ConfigFieldKind.String => "string",
		ConfigFieldKind.Select => "select",
		_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
	};
}

/// <summary>Вариант выбора. Значения всех выборов в схеме целые.</summary>
public sealed record ConfigOption(long Value, Phrase Label);

/// <summary>Раздел формы настроек.</summary>
public sealed record ConfigCategory(string Id, Phrase Label);

/// <summary>
/// Поле типизированной схемы <c>serverconfig.json</c>.
/// </summary>
/// <remarks>
/// Схема описывает только то, что умеет показать форма, а не всё, что принимает игра.
/// Ключи вне схемы проходят без изменений: моды и новые версии игры добавляют свои.
/// </remarks>
public sealed record ConfigField
{
	public required string Key { get; init; }

	public required Phrase Label { get; init; }

	public required string Category { get; init; }

	public required ConfigFieldKind Kind { get; init; }

	public required Phrase Description { get; init; }

	public double? Minimum { get; init; }

	public double? Maximum { get; init; }

	public IReadOnlyList<ConfigOption> Options { get; init; } = [];

	public bool RestartRequired { get; init; } = true;

	public bool Nullable { get; init; }

	public int? MaximumLength { get; init; }

	/// <summary>
	/// Шаблон всей строки. <c>$</c> в .NET пропускает перевод строки в конце, поэтому шаблоны
	/// заканчиваются на <c>\z</c>.
	/// </summary>
	public Regex? Pattern { get; init; }

	/// <summary>
	/// Проверка одного значения. Порядок и тексты как в <c>_validate_field</c>: тип, границы,
	/// затем длина и формат.
	/// </summary>
	public Result<Done> Check(JsonNode? value)
	{
		if (value is null || value.GetValueKind() == JsonValueKind.Null)
		{
			return Nullable ? Done.Ok : Failure.Invalid($"{Label}: неверный тип значения");
		}

		if (value is not JsonValue scalar || !HasKind(scalar))
		{
			return Failure.Invalid($"{Label}: неверный тип значения");
		}

		var kind = scalar.GetValueKind();

		if (kind == JsonValueKind.Number)
		{
			var number = double.Parse(scalar.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);

			if (Minimum is { } minimum && number < minimum)
			{
				return Failure.Invalid($"{Label}: значение меньше {PythonJson.FormatGeneral(minimum)}");
			}

			if (Maximum is { } maximum && number > maximum)
			{
				return Failure.Invalid($"{Label}: значение больше {PythonJson.FormatGeneral(maximum)}");
			}
		}

		if (kind == JsonValueKind.String)
		{
			var text = scalar.GetValue<string>();

			// Длина в кодовых точках, а не в единицах UTF-16.
			if (MaximumLength is { } limit && text.EnumerateRunes().Count() > limit)
			{
				return Failure.Invalid($"{Label}: текст длиннее {limit} символов");
			}

			if (Pattern is not null && !Pattern.IsMatch(text))
			{
				return Failure.Invalid($"{Label}: недопустимый формат");
			}
		}

		return Done.Ok;
	}

	private bool HasKind(JsonValue value)
	{
		var kind = value.GetValueKind();

		return Kind switch
		{
			ConfigFieldKind.Boolean => kind is JsonValueKind.True or JsonValueKind.False,
			ConfigFieldKind.Integer => PythonJson.IsIntegerToken(value),
			ConfigFieldKind.Number => kind == JsonValueKind.Number && IsFinite(value),
			ConfigFieldKind.String => kind == JsonValueKind.String,

			// Выбор сравнивает и значение, и тип: true не считается вариантом 1, поэтому сначала
			// проверяется, что значение целое.
			ConfigFieldKind.Select => PythonJson.IsIntegerToken(value)
				&& long.TryParse(value.ToJsonString(), out var chosen)
				&& Options.Any(option => option.Value == chosen),
			_ => false,
		};
	}

	private static bool IsFinite(JsonValue value) =>
		double.TryParse(
			value.ToJsonString(),
			System.Globalization.NumberStyles.Float,
			System.Globalization.CultureInfo.InvariantCulture,
			out var number)
		&& double.IsFinite(number);
}

/// <summary>
/// Схема формы настроек сервера, те же поля, что в <c>FIELDS</c> в <c>app/server_config.py</c>.
/// Порядок важен: проверка сообщает о первом неверном поле в порядке схемы, а тексты
/// сравниваются с золотыми фикстурами.
/// </summary>
public static class ConfigSchema
{
	/// <summary>Ключи, значения которых дифф не показывает.</summary>
	public const string PasswordKey = "Password";

	/// <summary>Замена скрытого значения.</summary>
	public const string Mask = "••••••";

	public static IReadOnlyList<ConfigCategory> Categories { get; } =
	[
		new("general", Phrase.From("Основные")),
		new("gameplay", Phrase.From("Игровой процесс")),
		new("performance", Phrase.From("Производительность")),
		new("security", Phrase.From("Безопасность и устойчивость")),
	];

	public static IReadOnlyList<ConfigField> Fields { get; } =
	[
		Text("ServerName", Phrase.From("Название сервера"), "general", Phrase.From("Отображаемое название сервера."), 120),
		Text("ServerUrl", Phrase.From("Сайт сервера"), "general", Phrase.From("Ссылка, показываемая вместе с описанием сервера."), 500, nullable: true),
		Text("ServerDescription", Phrase.From("Описание"), "general", Phrase.From("Краткое описание сервера."), 500, nullable: true),
		Text("WelcomeMessage", Phrase.From("Приветствие"), "general", Phrase.From("Сообщение при входе; {0} заменяется именем игрока."), 500),
		Integer("Port", Phrase.From("Порт"), "general", Phrase.From("TCP/UDP-порт игрового сервера."), 1, 65535),
		Integer("MaxClients", Phrase.From("Максимум игроков"), "general", Phrase.From("Число одновременно подключённых игроков."), 1, 1000),
		Integer("MaxClientsInQueue", Phrase.From("Размер очереди"), "general", Phrase.From("Сколько игроков может ожидать свободное место."), 0, 1000),
		Text("ServerLanguage", Phrase.From("Язык сервера"), "general", Phrase.From("Код языка, например en или ru."), 16, pattern: @"^[A-Za-z-]{2,16}\z"),
		Text("Password", Phrase.From("Пароль сервера"), "general", Phrase.From("Пароль для входа на сервер. Пустое значение — вход без пароля."), 200, nullable: true),
		Text("Ip", Phrase.From("Адрес привязки"), "general", Phrase.From("IP-адрес, на котором слушает сервер. Пусто — все адреса."), 64, nullable: true),
		Text("StartupCommands", Phrase.From("Команды при запуске"), "general", Phrase.From("Команды консоли, выполняемые сразу после старта сервера, по одной в строке."), 20_000, nullable: true),
		Flag("AdvertiseServer", Phrase.From("Публиковать в списке"), "general", Phrase.From("Показывать сервер в публичном списке.")),
		Flag("Upnp", Phrase.From("Автонастройка UPnP"), "general", Phrase.From("Пытаться автоматически пробросить игровой порт на роутере.")),
		Flag("CompressPackets", Phrase.From("Сжимать пакеты"), "general", Phrase.From("Использовать сжатие сетевого трафика.")),
		Integer("WarnClientsAfterAfkSeconds", Phrase.From("Предупреждать об AFK, сек"), "general", Phrase.From("Через сколько секунд бездействия предупредить игрока; 0 отключает."), 0, 86_400),
		Integer("KickClientsAfterAfkSeconds", Phrase.From("Отключать за AFK, сек"), "general", Phrase.From("Через сколько секунд бездействия отключать игрока; 0 отключает."), 0, 604_800),
		Flag("PassTimeWhenEmpty", Phrase.From("Время без игроков"), "gameplay", Phrase.From("Продолжать течение игрового времени, когда сервер пуст.")),
		Flag("AllowPvP", Phrase.From("PvP"), "gameplay", Phrase.From("Разрешить игрокам наносить урон друг другу.")),
		Flag("AllowFireSpread", Phrase.From("Распространение огня"), "gameplay", Phrase.From("Разрешить огню распространяться.")),
		Flag("AllowFallingBlocks", Phrase.From("Падающие блоки"), "gameplay", Phrase.From("Включить падение песка, гравия и одиночных камней.")),
		Text("DefaultRoleCode", Phrase.From("Роль новых игроков"), "gameplay", Phrase.From("Код роли, назначаемой при первом входе."), 64, pattern: @"^[A-Za-z0-9_-]+\z"),
		Flag("OnlyWhitelisted", Phrase.From("Только белый список (устарело)"), "security", Phrase.From("Устаревший параметр: начиная с Vintage Story 1.20 он не управляет доступом. Используйте WhitelistMode."), restartRequired: false),
		new ConfigField
		{
			Key = "WhitelistMode",
			Label = Phrase.From("Белый список"),
			Category = "security",
			Kind = ConfigFieldKind.Select,
			Description = Phrase.From("Кто может войти: 0 — режим по умолчанию (на выделенном сервере белый список включён), 1 — выключен, 2 — включён."),
			Options = [new(0, Phrase.From("По умолчанию")), new(1, Phrase.From("Выключен")), new(2, Phrase.From("Включён"))],
		},
		Flag("VerifyPlayerAuth", Phrase.From("Проверять учётную запись Vintage Story"), "security", Phrase.From("При входе проверять игрока через систему авторизации Vintage Story. Для обычного защищённого сервера оставьте включённым.")),
		Flag("LoginFloodProtection", Phrase.From("Защита входа"), "security", Phrase.From("Ограничивать поток попыток подключения.")),
		Flag("TemporaryIpBlockList", Phrase.From("Временная блокировка IP"), "security", Phrase.From("Временно блокировать подозрительные IP.")),
		Flag("CorruptionProtection", Phrase.From("Защита от повреждений"), "security", Phrase.From("Включить защитные проверки данных мира.")),
		Flag("RegenerateCorruptChunks", Phrase.From("Восстанавливать повреждённые чанки"), "security", Phrase.From("Разрешить автоматическую регенерацию повреждённых чанков.")),
		Flag("LogBlockBreakPlace", Phrase.From("Журнал блоков"), "security", Phrase.From("Записывать установку и разрушение блоков.")),
		Integer("DieBelowDiskSpaceMb", Phrase.From("Минимум свободного места, МБ"), "security", Phrase.From("Остановить сервер при меньшем объёме свободного места."), 100, 10_000_000),
		Integer("DieAboveMemoryUsageMb", Phrase.From("Лимит памяти, МБ"), "security", Phrase.From("Остановить сервер после превышения лимита памяти."), 256, 10_000_000),
		Integer("MaxChunkRadius", Phrase.From("Радиус чанков"), "performance", Phrase.From("Максимальный радиус загружаемых чанков вокруг игрока."), 1, 64),
		Integer("ClientConnectionTimeout", Phrase.From("Тайм-аут клиента, сек"), "performance", Phrase.From("Ожидание клиента до признания соединения потерянным."), 10, 3600),
		Real("TickTime", Phrase.From("Целевое время тика, мс"), "performance", Phrase.From("Плановая длительность серверного тика."), 10, 100),
		Real("SpawnCapPlayerScaling", Phrase.From("Масштаб существ на игрока"), "performance", Phrase.From("Рост лимита существ при увеличении числа игроков."), 0, 1),
		Integer("RandomBlockTicksPerChunk", Phrase.From("Случайных тиков на чанк"), "performance", Phrase.From("Количество случайно обновляемых блоков за проход."), 0, 10_000),
		Integer("MaxMainThreadBlockTicks", Phrase.From("Лимит block ticks"), "performance", Phrase.From("Максимум обновлений блоков за серверный тик."), 100, 10_000_000),
		Integer("BlockTickInterval", Phrase.From("Интервал block ticks, мс"), "performance", Phrase.From("Интервал системы случайных обновлений блоков."), 10, 3_600_000),
	];

	private static readonly Dictionary<string, ConfigField> byKey =
		Fields.ToDictionary(field => field.Key, StringComparer.Ordinal);

	/// <summary>Поле схемы по ключу файла, если ключ в схеме есть.</summary>
	internal static ConfigField? FindFor(string key) => byKey.GetValueOrDefault(key);

	private static ConfigField Flag(string key, Phrase label, string category, Phrase description, bool restartRequired = true) => new()
	{
		Key = key,
		Label = label,
		Category = category,
		Kind = ConfigFieldKind.Boolean,
		Description = description,
		RestartRequired = restartRequired,
	};

	private static ConfigField Integer(string key, Phrase label, string category, Phrase description, double minimum, double maximum) => new()
	{
		Key = key,
		Label = label,
		Category = category,
		Kind = ConfigFieldKind.Integer,
		Description = description,
		Minimum = minimum,
		Maximum = maximum,
	};

	private static ConfigField Real(string key, Phrase label, string category, Phrase description, double minimum, double maximum) => new()
	{
		Key = key,
		Label = label,
		Category = category,
		Kind = ConfigFieldKind.Number,
		Description = description,
		Minimum = minimum,
		Maximum = maximum,
	};

	private static ConfigField Text(
		string key,
		Phrase label,
		string category,
		Phrase description,
		int maximumLength,
		bool nullable = false,
		string? pattern = null) => new()
		{
			Key = key,
			Label = label,
			Category = category,
			Kind = ConfigFieldKind.String,
			Description = description,
			MaximumLength = maximumLength,
			Nullable = nullable,
			Pattern = pattern is null ? null : new Regex(pattern, RegexOptions.CultureInvariant),
		};
}
