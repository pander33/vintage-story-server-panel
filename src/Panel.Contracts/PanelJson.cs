using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Panel.Contracts;

/// <summary>
/// Общие настройки сериализации панели.
/// </summary>
/// <remarks>
/// Кириллица не превращается в <c>\uXXXX</c>-последовательности. Имена полей заданы
/// атрибутами. ADR-18/19 разрешают менять HTTP вместе с фронтендом; состояние прежних
/// установок продолжает читаться при обновлении.
/// </remarks>
public static class PanelJson
{
	public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
	{
		PropertyNamingPolicy = null,
		DefaultIgnoreCondition = JsonIgnoreCondition.Never,
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		NumberHandling = JsonNumberHandling.AllowReadingFromString,
		WriteIndented = false,
		TypeInfoResolver = JsonTypeInfoResolver.Combine(PanelJsonContext.Default, new DefaultJsonTypeInfoResolver()),
	};

	/// <summary>
	/// То же с отступами, для файлов, которые читает и правит человек: <c>serverconfig.json</c>,
	/// настройки модов, профили. Без отступов первое же сохранение из панели превратило бы файл в
	/// одну строку.
	/// </summary>
	public static JsonSerializerOptions Indented { get; } = new(Options) { WriteIndented = true };
}
