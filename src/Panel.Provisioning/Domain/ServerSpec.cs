using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Panel.SharedKernel;

namespace Panel.Provisioning.Domain;

/// <summary>
/// Ответы о новом сервере как они пришли: поля тела запроса без проверки.
/// </summary>
/// <remarks>
/// Отдельный тип, а не <see cref="ServerSpec"/> с непроверенными полями: описание проверяется
/// в трёх местах по-разному (мастер, <c>/api/provision/create</c>, демон), и непроверенное не
/// должно выглядеть проверенным.
/// </remarks>
public sealed record ServerAnswers
{
	public string Id { get; init; } = string.Empty;
	public string Name { get; init; } = string.Empty;
	public string Version { get; init; } = string.Empty;
	public long Port { get; init; }
	public string ServerName { get; init; } = string.Empty;
	public string Description { get; init; } = string.Empty;
	public long MaxPlayers { get; init; }
	public bool Whitelist { get; init; }
	public bool Pvp { get; init; }
	public string Language { get; init; } = string.Empty;
	public string WorldName { get; init; } = string.Empty;
	public string Seed { get; init; } = string.Empty;
	public string Playstyle { get; init; } = string.Empty;
}

/// <summary>
/// Описание сервера, который нужно создать.
/// </summary>
/// <remarks>
/// <para>
/// Проверок три, и они разные; фронтенд на это рассчитывает:
/// </para>
/// <list type="bullet">
/// <item><see cref="FromWizard"/> — мастер: списки языков и стилей, длины полей, умолчания для
/// пустых имён;</item>
/// <item><see cref="FromProvisionRequest"/> — создание из «Обзора»: только идентификатор, порт
/// и число игроков, остальное проверит демон;</item>
/// <item><see cref="InstanceSpecification.Parse"/> — демон: закрытый набор ключей и свои,
/// более широкие пределы.</item>
/// </list>
/// <para>
/// Привилегированная сторона полагается только на последнюю.
/// </para>
/// </remarks>
public sealed record ServerSpec
{
	public const int DefaultPort = 42420;

	public const string DefaultWorldName = "Новый мир";

	/// <summary>Языки мастера в порядке показа в форме.</summary>
	public static IReadOnlyList<string> Languages { get; } = ["ru", "en", "de", "fr", "es", "zh"];

	/// <summary>Стили игры.</summary>
	public static IReadOnlyList<string> Playstyles { get; } =
		["surviveandbuild", "exploration", "wildernesssurvival", "homosapiens", "creativebuilding"];

	private ServerSpec()
	{
	}

	public InstanceId Id { get; private init; }
	public string Name { get; private init; } = string.Empty;
	public string Version { get; private init; } = string.Empty;
	public int Port { get; private init; }
	public string ServerName { get; private init; } = string.Empty;
	public string Description { get; private init; } = string.Empty;
	public int MaxPlayers { get; private init; }
	public bool Whitelist { get; private init; }
	public bool Pvp { get; private init; }
	public string Language { get; private init; } = string.Empty;
	public string WorldName { get; private init; } = string.Empty;
	public string Seed { get; private init; } = string.Empty;
	public string Playstyle { get; private init; } = string.Empty;

	/// <summary>
	/// Проверка мастера. Пустое имя заменяется идентификатором, пустое имя в списке серверов —
	/// именем, пустое имя мира — «Новый мир».
	/// </summary>
	public static Result<ServerSpec> FromWizard(ServerAnswers answers)
	{
		ArgumentNullException.ThrowIfNull(answers);

		var id = SafeId(answers.Id);
		if (id is null)
		{
			return Failure.Invalid(
				"Идентификатор: строчные латинские буквы, цифры, «-» и «_», до 32 символов, начиная с буквы или цифры");
		}

		if (answers.Port is < 1024 or > 65535)
		{
			return Failure.Invalid("Игровой порт должен быть от 1024 до 65535");
		}

		if (answers.Version.Length == 0)
		{
			return Failure.Invalid("Не выбрана версия игры");
		}

		if (!Playstyles.Contains(answers.Playstyle))
		{
			return Failure.Invalid("Неизвестный стиль игры");
		}

		if (!Languages.Contains(answers.Language))
		{
			return Failure.Invalid("Неизвестный язык сервера");
		}

		if (answers.MaxPlayers is < 1 or > 512)
		{
			return Failure.Invalid("Число игроков должно быть от 1 до 512");
		}

		(string Field, string Value, int Limit)[] limits =
		[
			("name", answers.Name, 64),
			("server_name", answers.ServerName, 64),
			("description", answers.Description, 256),
			("world_name", answers.WorldName, 64),
			("seed", answers.Seed, 64),
		];
		foreach (var (field, value, limit) in limits)
		{
			if (PythonLength(value) > limit)
			{
				return Failure.Invalid($"Слишком длинное значение поля {field}");
			}
		}

		var name = answers.Name.Trim() is { Length: > 0 } given ? given : id.Value.Value;
		return new ServerSpec
		{
			Id = id.Value,
			Name = name,
			Version = answers.Version,
			Port = (int) answers.Port,
			ServerName = answers.ServerName.Trim() is { Length: > 0 } shown ? shown : name,
			Description = answers.Description,
			MaxPlayers = (int) answers.MaxPlayers,
			Whitelist = answers.Whitelist,
			Pvp = answers.Pvp,
			Language = answers.Language,
			WorldName = answers.WorldName.Trim() is { Length: > 0 } world ? world : DefaultWorldName,
			Seed = answers.Seed,
			Playstyle = answers.Playstyle,
		};
	}

	/// <summary>
	/// Проверка запроса на создание: идентификатор, порт, число игроков. Языки, стили, длины и
	/// версию проверяет демон, и его отказ становится итогом операции, а не ответом маршрута.
	/// </summary>
	public static Result<ServerSpec> FromProvisionRequest(ServerAnswers answers)
	{
		ArgumentNullException.ThrowIfNull(answers);

		if (SafeId(answers.Id) is not { } id)
		{
			return Failure.Conflict("Недопустимый идентификатор сервера");
		}

		if (answers.Port is < 1024 or > 65535)
		{
			return Failure.Conflict("Игровой порт должен быть от 1024 до 65535");
		}

		if (answers.MaxPlayers is < 1 or > 512)
		{
			return Failure.Conflict("Число игроков должно быть от 1 до 512");
		}

		return new ServerSpec
		{
			Id = id,
			Name = answers.Name,
			Version = answers.Version,
			Port = (int) answers.Port,
			ServerName = answers.ServerName,
			Description = answers.Description,
			MaxPlayers = (int) answers.MaxPlayers,
			Whitelist = answers.Whitelist,
			Pvp = answers.Pvp,
			Language = answers.Language,
			WorldName = answers.WorldName,
			Seed = answers.Seed,
			Playstyle = answers.Playstyle,
		};
	}

	/// <summary>
	/// Описание из <c>setup.json</c> (<c>draft.server</c>). Поле неожиданного типа — отказ: черновик
	/// мог быть изменён вручную.
	/// </summary>
	public static Result<ServerSpec> FromDraft(JsonObject? draft)
	{
		if (draft is null || draft.Count == 0)
		{
			return Failure.Invalid("Сначала заполните настройки сервера");
		}

		try
		{
			return FromProvisionRequest(new ServerAnswers
			{
				Id = Text(draft["id"]),
				Name = Text(draft["name"]),
				Version = Text(draft["version"]),
				Port = Number(draft["port"]),
				ServerName = Text(draft["server_name"]),
				Description = Text(draft["description"]),
				MaxPlayers = Number(draft["max_players"]),
				Whitelist = Flag(draft["whitelist"]),
				Pvp = Flag(draft["pvp"]),
				Language = Text(draft["language"]),
				WorldName = Text(draft["world_name"]),
				Seed = Text(draft["seed"]),
				Playstyle = Text(draft["playstyle"]),
			});
		}
		catch (Exception exception) when (exception is InvalidOperationException or FormatException)
		{
			return Failure.Invalid("Черновик настроек сервера повреждён");
		}
	}

	/// <summary>Описание вместе с именем пакета, которое отправляется демону в <c>instance-create</c>.</summary>
	public JsonObject ToJson(StagedFile? package = null)
	{
		var value = new JsonObject
		{
			["id"] = Id.Value,
			["name"] = Name,
			["version"] = Version,
			["port"] = Port,
			["server_name"] = ServerName,
			["description"] = Description,
			["max_players"] = MaxPlayers,
			["whitelist"] = Whitelist,
			["pvp"] = Pvp,
			["language"] = Language,
			["world_name"] = WorldName,
			["seed"] = Seed,
			["playstyle"] = Playstyle,
		};

		if (package is { } staged)
		{
			value["package"] = staged.Name;
		}

		return value;
	}

	internal static ServerSpec Trusted(
		InstanceId id,
		string name,
		string version,
		int port,
		string serverName,
		string description,
		int maxPlayers,
		bool whitelist,
		bool pvp,
		string language,
		string worldName,
		string seed,
		string playstyle) => new()
		{
			Id = id,
			Name = name,
			Version = version,
			Port = port,
			ServerName = serverName,
			Description = description,
			MaxPlayers = maxPlayers,
			Whitelist = whitelist,
			Pvp = pvp,
			Language = language,
			WorldName = worldName,
			Seed = seed,
			Playstyle = playstyle,
		};

	/// <summary><c>INSTANCE_RE.fullmatch</c>: без обрезки пробелов и без «-», которым адресуется реестр.</summary>
	private static InstanceId? SafeId(string raw)
	{
		if (raw != raw.Trim())
		{
			return null;
		}

		var id = InstanceId.Create(raw);
		return id.IsSuccess && !id.Value.IsRegistry ? id.Value : null;
	}

	/// <summary>Длина в кодовых точках, а не в единицах UTF-16.</summary>
	internal static int PythonLength(string value) => value.EnumerateRunes().Count();

	private static string Text(JsonNode? node) => node switch
	{
		null => string.Empty,
		JsonValue value when value.TryGetValue<string>(out var text) => text,
		_ => throw new InvalidOperationException(),
	};

	private static long Number(JsonNode? node) =>
		InstanceSpecification.Whole(node) ?? throw new InvalidOperationException();

	private static bool Flag(JsonNode? node) => node switch
	{
		JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
		_ => throw new InvalidOperationException(),
	};
}

/// <summary>
/// Описание сервера для демона. Набор ключей закрыт, лишний или недостающий ключ — отказ.
/// </summary>
public static partial class InstanceSpecification
{
	public static IReadOnlySet<string> Keys { get; } = new HashSet<string>(StringComparer.Ordinal)
	{
		"id", "name", "version", "port", "server_name", "description", "max_players",
		"whitelist", "pvp", "language", "world_name", "seed", "playstyle", "package",
	};

	[GeneratedRegex(@"\A\d{1,3}\.\d{1,3}\.\d{1,4}(?:-[A-Za-z0-9.]{1,16})?\z", RegexOptions.CultureInvariant)]
	public static partial Regex GameVersion { get; }

	[GeneratedRegex(@"\A[A-Za-z-]{2,16}\z", RegexOptions.CultureInvariant)]
	private static partial Regex LanguagePattern { get; }

	/// <summary>Описание и имя пакета в staging. Тексты отказов английские.</summary>
	public static Result<(ServerSpec Spec, string Package)> Parse(JsonNode? raw)
	{
		if (raw is not JsonObject value)
		{
			return Failure.Invalid("Instance specification must be a JSON object");
		}

		var unknown = value.Select(pair => pair.Key).Where(key => !Keys.Contains(key)).Order(StringComparer.Ordinal).ToList();
		if (unknown.Count > 0)
		{
			return Failure.Invalid($"Unknown keys in instance specification: {string.Join(", ", unknown)}");
		}

		var missing = Keys.Where(key => !value.ContainsKey(key)).Order(StringComparer.Ordinal).ToList();
		if (missing.Count > 0)
		{
			return Failure.Invalid($"Missing keys in instance specification: {string.Join(", ", missing)}");
		}

		var id = InstanceId.Create(Text(value["id"]));
		if (!id.IsSuccess || id.Value.IsRegistry || Text(value["id"]) != id.Value.Value)
		{
			return Failure.Invalid("Unsafe instance id");
		}

		var name = SafeText(Text(value["name"]), 64, "instance name");
		var version = Text(value["version"]);
		if (!GameVersion.IsMatch(version))
		{
			return Failure.Invalid("Invalid game version");
		}

		if (Integer(value["port"]) is not { } port || Integer(value["max_players"]) is not { } players)
		{
			return Failure.Invalid("Port and player count must be integers");
		}

		if (port is < 1024 or > 65535)
		{
			return Failure.Invalid("Port must be between 1024 and 65535");
		}

		if (players is < 1 or > 1000)
		{
			return Failure.Invalid("Player count must be between 1 and 1000");
		}

		var serverName = SafeText(Text(value["server_name"]), 120, "server name");
		var description = SafeText(Text(value["description"]), 500, "server description", allowEmpty: true);
		if (!IsBool(value["whitelist"]) || !IsBool(value["pvp"]))
		{
			return Failure.Invalid("Whitelist and PvP must be booleans");
		}

		var language = Text(value["language"]);
		if (!LanguagePattern.IsMatch(language))
		{
			return Failure.Invalid("Invalid server language");
		}

		var world = SafeText(Text(value["world_name"]), 80, "world name");
		var seed = SafeText(Text(value["seed"]), 80, "seed", allowEmpty: true);
		var playstyle = Text(value["playstyle"]);
		if (!ServerSpec.Playstyles.Contains(playstyle))
		{
			return Failure.Invalid("Invalid playstyle");
		}

		foreach (var text in new[] { name, serverName, description, world, seed })
		{
			if (!text.IsSuccess)
			{
				return text.Failure!;
			}
		}

		var spec = ServerSpec.Trusted(
			id.Value,
			name.Value,
			version,
			(int) port,
			serverName.Value,
			description.Value,
			(int) players,
			value["whitelist"]!.GetValue<bool>(),
			value["pvp"]!.GetValue<bool>(),
			language,
			world.Value,
			seed.Value,
			playstyle);
		return (spec, Text(value["package"]));
	}

	/// <summary>Обрезка, непустое (если пустое не разрешено), предел длины, без управляющих символов.</summary>
	private static Result<string> SafeText(string raw, int maximum, string label, bool allowEmpty = false)
	{
		var value = raw.Trim();
		return (value.Length == 0 && !allowEmpty) || ServerSpec.PythonLength(value) > maximum || value.Any(symbol => symbol < ' ')
			? Failure.Invalid($"Invalid {label}")
			: value;
	}

	/// <summary>Строка как есть; для остального текст JSON.</summary>
	private static string Text(JsonNode? node) => node switch
	{
		null => "None",
		JsonValue value when value.TryGetValue<string>(out var text) => text,
		_ => node.ToJsonString(),
	};

	/// <summary>
	/// Число JSON как целое, откуда бы ни взялся узел: <c>TryGetValue&lt;long&gt;</c> не читает
	/// значение, созданное из <c>int</c>. Дробная часть отбрасывается, как <c>int(3.5)</c>.
	/// </summary>
	internal static long? Whole(JsonNode? node) =>
		node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number
		&& double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
			? (long) number
			: null;

	private static long? Integer(JsonNode? node) => node switch
	{
		JsonValue value when Whole(value) is { } number => number,
		JsonValue value when value.TryGetValue<bool>(out var flag) => flag ? 1 : 0,
		JsonValue value when value.TryGetValue<string>(out var text)
							 && long.TryParse(text.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
		_ => null,
	};

	private static bool IsBool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out _);
}
