using Panel.Agent.Content;
using Panel.Configuration.Domain;
using Panel.Configuration.Localization;
using Panel.Contracts;
using Panel.Contracts.Agent;
using Panel.Contracts.Configuration;
using Panel.Fleet.Domain;

namespace Panel.Agent.Transport;

/// <summary>
/// Схема параметров мира: игра плюс включённые моды, с уже разрешёнными подписями. Демон знает
/// каталог программы и модов; панель присылает только идентификатор сервера.
/// </summary>
public sealed class WorldSchemaOperations : IServerOperations
{
	private readonly WorldSchemaReader reader;

	public WorldSchemaOperations(WorldSchemaReader reader)
	{
		ArgumentNullException.ThrowIfNull(reader);
		this.reader = reader;
	}

	public static bool Handles(string op) => op == AgentProtocol.OpWorldConfigSchema;

	bool IServerOperations.Serves(string op) => Handles(op);

	public Task<AgentReply> ExecuteAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
	{
		// Слияние источников никогда не отказывает целиком: отказ одного источника уходит в
		// Warnings, а не в Failure (см. WorldSchema.Merge).
		var schema = reader.Read(server).Value;

		// Язык подписей — язык интерфейса, из которого пришёл запрос. Код стал бы именем файла, поэтому
		// разбирается в закрытый набор, а не подставляется в путь: всё, кроме «ru», — английский.
		// Английскому запасной язык тоже английский: русская подпись в английском интерфейсе хуже кода.
		var language = PhraseCatalog.Parse(request.Args.Count > 0 ? request.Args[0] : null) == PanelLanguage.Russian
			? reader.ReadLanguage(server, "ru", "en")
			: reader.ReadLanguage(server, "en", "en");
		var payload = ToPayload(schema, language);
		return Task.FromResult(AgentReply.Success(System.Text.Json.JsonSerializer.SerializeToNode(payload, PanelJson.Options)));
	}

	private static WorldSchemaPayload ToPayload(WorldSchema schema, WorldLanguagePack language)
	{
		var playstyles = schema.Playstyles.Select(style => new WorldPlaystylePayload
		{
			Code = style.Code,
			Label = language.Label($"playstyle-{style.LanguageCode}", style.Code),
			Description = language.Find($"playstyle-desc-{style.LanguageCode}") ?? string.Empty,
			PlaylistCode = style.PlayListCode,
			WorldType = style.WorldType,
			Mods = style.Mods,
			Values = style.Values,
		}).ToList();

		var attributes = schema.Attributes.Select(attribute => new WorldAttributePayload
		{
			Code = attribute.Code,
			Label = language.Label($"worldattribute-{attribute.Code}", attribute.Code),
			Description = language.Find($"worldattribute-{attribute.Code}-desc") ?? string.Empty,
			DataType = DataTypeWire(attribute.Type),
			Category = attribute.Category,
			Options = Options(attribute, language),
			Default = attribute.Default,
			OnlyDuringWorldCreate = attribute.OnlyDuringWorldCreate,
		}).ToList();

		var categories = attributes
			.Select(attribute => attribute.Category)
			.Distinct(StringComparer.Ordinal)
			.OrderBy(id => id, StringComparer.Ordinal)
			.Select(id => new WorldCategoryPayload { Id = id, Label = language.Label($"worldconfig-category-{id}", id) })
			.ToList();

		return new WorldSchemaPayload
		{
			Playstyles = playstyles,
			Categories = categories,
			Attributes = attributes,
			Warnings = schema.Warnings,
		};
	}

	private static IReadOnlyList<WorldAttributeOptionPayload> Options(WorldAttribute attribute, WorldLanguagePack language)
	{
		if (attribute.Type != WorldAttributeType.Dropdown)
		{
			return [];
		}

		var options = new List<WorldAttributeOptionPayload>(attribute.Values.Count);
		for (var index = 0; index < attribute.Values.Count; index++)
		{
			var value = attribute.Values[index];
			var fallbackName = index < attribute.Names.Count ? attribute.Names[index] : value;
			options.Add(new WorldAttributeOptionPayload
			{
				Value = value,
				Label = language.Label($"worldconfig-{attribute.Code}-{fallbackName}", fallbackName),
			});
		}

		return options;
	}

	private static string DataTypeWire(WorldAttributeType type) => type switch
	{
		WorldAttributeType.Bool => "bool",
		WorldAttributeType.Dropdown => "dropdown",
		WorldAttributeType.Integer => "integer",
		WorldAttributeType.Number => "number",
		WorldAttributeType.Text => "text",
		_ => "unknown",
	};
}
