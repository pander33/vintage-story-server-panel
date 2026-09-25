using Panel.Configuration.Domain;
using Panel.Contracts.Http;

namespace Panel.Web.Application;

/// <summary>Доменные типы настроек → форма ответа.</summary>
public static class ConfigurationMapping
{
	private static readonly ConfigSchemaResponse cachedSchema = new()
	{
		Categories = [.. ConfigSchema.Categories.Select(category => new ConfigCategoryResponse
		{
			Id = category.Id,
			Label = category.Label,
		})],
		Fields = [.. ConfigSchema.Fields.Select(field => new ConfigFieldResponse
		{
			Key = field.Key,
			Label = field.Label,
			Category = field.Category,
			Type = ConfigFieldKindWire.ToWire(field.Kind),
			Description = field.Description,
			Min = field.Minimum,
			Max = field.Maximum,
			Options = [.. field.Options.Select(option => new ConfigOptionResponse
			{
				Value = option.Value,
				Label = option.Label,
			})],
			RestartRequired = field.RestartRequired,
			Nullable = field.Nullable,
			MaximumLength = field.MaximumLength,
		})],
	};

	/// <summary>Схема не меняется, поэтому собирается один раз.</summary>
	public static ConfigSchemaResponse Schema() => cachedSchema;

	public static ConfigChangeResponse Change(ConfigChange change) => new()
	{
		Key = change.Key,
		Label = change.Label,
		Before = change.Before?.DeepClone(),
		After = change.After?.DeepClone(),
		RestartRequired = change.RestartRequired,
	};

	public static ApplyStateResponse State(ApplyState state) => new()
	{
		State = ApplyWire.ToWire(state.State),
		Active = state.Active,
		RestartRequired = state.RestartRequired,
		ChangedKeys = state.ChangedKeys,
		SavedAt = state.SavedAt,
		Source = ApplyWire.ToWire(state.Source),
	};
}
