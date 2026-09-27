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
public sealed record ConfigOption(long Value, string Label);

/// <summary>Раздел формы настроек.</summary>
public sealed record ConfigCategory(string Id, string Label);

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

	public required string Label { get; init; }

	public required string Category { get; init; }

	public required ConfigFieldKind Kind { get; init; }

	public required string Description { get; init; }

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
		new("general", "Основные"),
		new("gameplay", "Игровой процесс"),
		new("performance", "Производительность"),
		new("security", "Безопасность и устойчивость"),
	];

	public static IReadOnlyList<ConfigField> Fields { get; } =
	[
		Text("ServerName", "Название сервера", "general", "Отображаемое название сервера.", 120),
		Text("ServerUrl", "Сайт сервера", "general", "Ссылка, показываемая вместе с описанием сервера.", 500, nullable: true),
		Text("ServerDescription", "Описание", "general", "Краткое описание сервера.", 500, nullable: true),
		Text("WelcomeMessage", "Приветствие", "general", "Сообщение при входе; {0} заменяется именем игрока.", 500),
		Integer("Port", "Порт", "general", "TCP/UDP-порт игрового сервера.", 1, 65535),
		Integer("MaxClients", "Максимум игроков", "general", "Число одновременно подключённых игроков.", 1, 1000),
		Integer("MaxClientsInQueue", "Размер очереди", "general", "Сколько игроков может ожидать свободное место.", 0, 1000),
		Text("ServerLanguage", "Язык сервера", "general", "Код языка, например en или ru.", 16, pattern: @"^[A-Za-z-]{2,16}\z"),
		Text("Password", "Пароль сервера", "general", "Пароль для входа на сервер. Пустое значение — вход без пароля.", 200, nullable: true),
		Text("Ip", "Адрес привязки", "general", "IP-адрес, на котором слушает сервер. Пусто — все адреса.", 64, nullable: true),
		Text("StartupCommands", "Команды при запуске", "general", "Команды консоли, выполняемые сразу после старта сервера, по одной в строке.", 20_000, nullable: true),
		Flag("AdvertiseServer", "Публиковать в списке", "general", "Показывать сервер в публичном списке."),
		Flag("Upnp", "Автонастройка UPnP", "general", "Пытаться автоматически пробросить игровой порт на роутере."),
		Flag("CompressPackets", "Сжимать пакеты", "general", "Использовать сжатие сетевого трафика."),
		Integer("WarnClientsAfterAfkSeconds", "Предупреждать об AFK, сек", "general", "Через сколько секунд бездействия предупредить игрока; 0 отключает.", 0, 86_400),
		Integer("KickClientsAfterAfkSeconds", "Отключать за AFK, сек", "general", "Через сколько секунд бездействия отключать игрока; 0 отключает.", 0, 604_800),
		Flag("PassTimeWhenEmpty", "Время без игроков", "gameplay", "Продолжать течение игрового времени, когда сервер пуст."),
		Flag("AllowPvP", "PvP", "gameplay", "Разрешить игрокам наносить урон друг другу."),
		Flag("AllowFireSpread", "Распространение огня", "gameplay", "Разрешить огню распространяться."),
		Flag("AllowFallingBlocks", "Падающие блоки", "gameplay", "Включить падение песка, гравия и одиночных камней."),
		Text("DefaultRoleCode", "Роль новых игроков", "gameplay", "Код роли, назначаемой при первом входе.", 64, pattern: @"^[A-Za-z0-9_-]+\z"),
		Flag("OnlyWhitelisted", "Только белый список (устарело)", "security", "Устаревший параметр: начиная с Vintage Story 1.20 он не управляет доступом. Используйте WhitelistMode.", restartRequired: false),
		new ConfigField
		{
			Key = "WhitelistMode",
			Label = "Белый список",
			Category = "security",
			Kind = ConfigFieldKind.Select,
			Description = "Кто может войти: 0 — режим по умолчанию (на выделенном сервере белый список включён), 1 — выключен, 2 — включён.",
			Options = [new(0, "По умолчанию"), new(1, "Выключен"), new(2, "Включён")],
		},
		Flag("VerifyPlayerAuth", "Проверять учётную запись Vintage Story", "security", "При входе проверять игрока через систему авторизации Vintage Story. Для обычного защищённого сервера оставьте включённым."),
		Flag("LoginFloodProtection", "Защита входа", "security", "Ограничивать поток попыток подключения."),
		Flag("TemporaryIpBlockList", "Временная блокировка IP", "security", "Временно блокировать подозрительные IP."),
		Flag("CorruptionProtection", "Защита от повреждений", "security", "Включить защитные проверки данных мира."),
		Flag("RegenerateCorruptChunks", "Восстанавливать повреждённые чанки", "security", "Разрешить автоматическую регенерацию повреждённых чанков."),
		Flag("LogBlockBreakPlace", "Журнал блоков", "security", "Записывать установку и разрушение блоков."),
		Integer("DieBelowDiskSpaceMb", "Минимум свободного места, МБ", "security", "Остановить сервер при меньшем объёме свободного места.", 100, 10_000_000),
		Integer("DieAboveMemoryUsageMb", "Лимит памяти, МБ", "security", "Остановить сервер после превышения лимита памяти.", 256, 10_000_000),
		Integer("MaxChunkRadius", "Радиус чанков", "performance", "Максимальный радиус загружаемых чанков вокруг игрока.", 1, 64),
		Integer("ClientConnectionTimeout", "Тайм-аут клиента, сек", "performance", "Ожидание клиента до признания соединения потерянным.", 10, 3600),
		Real("TickTime", "Целевое время тика, мс", "performance", "Плановая длительность серверного тика.", 10, 100),
		Real("SpawnCapPlayerScaling", "Масштаб существ на игрока", "performance", "Рост лимита существ при увеличении числа игроков.", 0, 1),
		Integer("RandomBlockTicksPerChunk", "Случайных тиков на чанк", "performance", "Количество случайно обновляемых блоков за проход.", 0, 10_000),
		Integer("MaxMainThreadBlockTicks", "Лимит block ticks", "performance", "Максимум обновлений блоков за серверный тик.", 100, 10_000_000),
		Integer("BlockTickInterval", "Интервал block ticks, мс", "performance", "Интервал системы случайных обновлений блоков.", 10, 3_600_000),
	];

	private static readonly Dictionary<string, ConfigField> byKey =
		Fields.ToDictionary(field => field.Key, StringComparer.Ordinal);

	/// <summary>Поле схемы по ключу файла, если ключ в схеме есть.</summary>
	internal static ConfigField? FindFor(string key) => byKey.GetValueOrDefault(key);

	private static ConfigField Flag(string key, string label, string category, string description, bool restartRequired = true) => new()
	{
		Key = key,
		Label = label,
		Category = category,
		Kind = ConfigFieldKind.Boolean,
		Description = description,
		RestartRequired = restartRequired,
	};

	private static ConfigField Integer(string key, string label, string category, string description, double minimum, double maximum) => new()
	{
		Key = key,
		Label = label,
		Category = category,
		Kind = ConfigFieldKind.Integer,
		Description = description,
		Minimum = minimum,
		Maximum = maximum,
	};

	private static ConfigField Real(string key, string label, string category, string description, double minimum, double maximum) => new()
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
		string label,
		string category,
		string description,
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
