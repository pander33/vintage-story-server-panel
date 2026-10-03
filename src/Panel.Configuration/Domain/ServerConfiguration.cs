using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Panel.SharedKernel;

namespace Panel.Configuration.Domain;

/// <summary>
/// Ревизия конфигурации: SHA-256 канонической формы строчными шестнадцатеричными цифрами.
/// </summary>
/// <remarks>
/// Её возвращает <c>GET /api/config</c>, её присылает редактор при сохранении, и она записана
/// в <c>config-apply.json</c>. Каноническая форма — <see cref="CanonicalJson"/>; в 0.3.0 и раньше
/// ревизия считалась от формы <c>json.dumps</c> Python. Отметка, записанная прежней версией, не совпадёт
/// ни с одной новой ревизией, и трекер один раз судит по времени правки файла (findings §82).
/// </remarks>
public readonly record struct ConfigRevision
{
	private ConfigRevision(string value) => Value = value;

	public string Value { get; }

	public static ConfigRevision From(JsonObject document)
	{
		ArgumentNullException.ThrowIfNull(document);

		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.Write(document)));
		return new ConfigRevision(Convert.ToHexStringLower(hash));
	}

	/// <summary>
	/// Ревизия из запроса. Пустая означает «не проверять».
	/// </summary>
	public static ConfigRevision? ParseOptional(string? raw) =>
		string.IsNullOrEmpty(raw) ? null : new ConfigRevision(raw);

	public override string ToString() => Value ?? string.Empty;
}

/// <summary>Одно отличие двух конфигураций в виде строки предпросмотра.</summary>
public sealed record ConfigChange(string Key, Phrase Label, JsonNode? Before, JsonNode? After, bool RestartRequired);

/// <summary>
/// Содержимое <c>serverconfig.json</c> одного сервера.
/// </summary>
/// <remarks>
/// Агрегат хранит документ целиком, а не только поля схемы: ключи вне схемы принадлежат игре
/// и модам и проходят через панель без изменений.
/// </remarks>
public sealed class ServerConfiguration
{
	/// <summary>Предел размера; считается по тексту с пробелами.</summary>
	public const int MaxBytes = 2 * 1024 * 1024;

	private const string worldSection = "WorldConfig";
	private const string templateKey = "WorldConfiguration";

	private readonly JsonObject document;

	private ServerConfiguration(JsonObject document)
	{
		this.document = document;
		Revision = ConfigRevision.From(document);
	}

	public ConfigRevision Revision { get; }

	/// <summary>Копия документа; собственный экземпляр агрегат наружу не отдаёт.</summary>
	public JsonObject Document => (JsonObject) document.DeepClone();

	/// <summary>
	/// Шаблон нового мира из <c>WorldConfig.WorldConfiguration</c>. Пустой, если раздела нет или
	/// это не объект.
	/// </summary>
	public WorldTemplate WorldTemplate =>
		document[worldSection] is JsonObject world && world[templateKey] is JsonObject template
			? WorldTemplate.Wrap((JsonObject) template.DeepClone())
			: WorldTemplate.Empty;

	public static Result<ServerConfiguration> FromJson(JsonNode? node) =>
		node is JsonObject document
			? new ServerConfiguration((JsonObject) document.DeepClone())
			: Failure.Invalid("Конфигурация должна быть JSON-объектом");

	/// <summary>
	/// Проверка по схеме. Сообщает о первом неверном поле в порядке схемы.
	/// </summary>
	public Result<Done> Validate()
	{
		if (Encoding.UTF8.GetByteCount(PythonJson.Spaced(document)) > MaxBytes)
		{
			return Failure.Invalid("Конфигурация превышает 2 МБ");
		}

		foreach (var field in ConfigSchema.Fields)
		{
			if (!document.TryGetPropertyValue(field.Key, out var value))
			{
				continue;
			}

			var checkedField = field.Check(value);
			if (!checkedField.IsSuccess)
			{
				return checkedField;
			}
		}

		return Done.Ok;
	}

	/// <summary>
	/// Предупреждение для формы. На старом или изменённом модами сервере значение может выходить
	/// за схему; редактор при этом работает, а отказ будет только при сохранении.
	/// </summary>
	public Phrase? ValidationWarning => Validate().Match<Phrase?>(_ => null, failure => failure.Text);

	/// <summary>
	/// Отличия этой конфигурации от следующей: ключи без учёта регистра, пароль скрыт.
	/// </summary>
	public IReadOnlyList<ConfigChange> DiffTo(ServerConfiguration next)
	{
		ArgumentNullException.ThrowIfNull(next);

		var keys = document.Select(pair => pair.Key)
			.Union(next.document.Select(pair => pair.Key), StringComparer.Ordinal)
			.OrderBy(key => key.ToLowerInvariant(), StringComparer.Ordinal)
			.ThenBy(key => key, StringComparer.Ordinal);

		var changes = new List<ConfigChange>();
		foreach (var key in keys)
		{
			var before = document[key];
			var after = next.document[key];

			if (JsonEquality.AreEqual(before, after))
			{
				continue;
			}

			var field = ConfigSchema.FindFor(key);
			changes.Add(new ConfigChange(
				key,
				field?.Label ?? Phrase.From(key),
				Shown(key, before),
				Shown(key, after),
				field?.RestartRequired ?? true));
		}

		return changes;
	}

	/// <summary>
	/// Та же конфигурация с другим шаблоном мира. Раздел <c>WorldConfig</c> создаётся, если его
	/// нет. Если он есть, но это не объект, возвращается отказ: перезапись уничтожила бы данные,
	/// которых панель не понимает.
	/// </summary>
	public Result<ServerConfiguration> WithWorldTemplate(WorldTemplate template)
	{
		ArgumentNullException.ThrowIfNull(template);

		var document = Document;
		if (!document.TryGetPropertyValue(worldSection, out var world) || world is null)
		{
			world = new JsonObject();
			document[worldSection] = world;
		}

		if (world is not JsonObject section)
		{
			return Failure.Conflict("WorldConfig имеет неподдерживаемый формат");
		}

		section[templateKey] = template.Document;
		return new ServerConfiguration(document);
	}

	/// <summary>
	/// Документ для роли без права на изменение: пароль входа на сервер — секрет администратора.
	/// </summary>
	public JsonObject RedactedDocument()
	{
		var document = Document;
		if (document[ConfigSchema.PasswordKey] is { } password && password.GetValueKind() != System.Text.Json.JsonValueKind.Null)
		{
			document[ConfigSchema.PasswordKey] = ConfigSchema.Mask;
		}

		return document;
	}

	private static JsonNode? Shown(string key, JsonNode? value)
	{
		if (value is null || value.GetValueKind() == System.Text.Json.JsonValueKind.Null)
		{
			return null;
		}

		return string.Equals(key, ConfigSchema.PasswordKey, StringComparison.Ordinal)
			? JsonValue.Create(ConfigSchema.Mask)
			: value.DeepClone();
	}
}
