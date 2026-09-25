using System.Text.Json;
using System.Text.Json.Serialization;

namespace Panel.Contracts;

/// <summary>
/// Общие настройки сериализации панели.
/// </summary>
/// <remarks>
/// ADR-04 и ADR-16: формат файлов состояния и форма HTTP-ответов заморожены. Кириллица в
/// ответах не превращается в <c>\uXXXX</c>-последовательности. Имена полей заданы атрибутами,
/// а не политикой именования, чтобы замороженный контракт не зависел от её правил.
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
	};

	/// <summary>
	/// То же с отступами, для файлов, которые читает и правит человек: <c>serverconfig.json</c>,
	/// настройки модов, профили. Без отступов первое же сохранение из панели превратило бы файл в
	/// одну строку.
	/// </summary>
	public static JsonSerializerOptions Indented { get; } = new(Options) { WriteIndented = true };
}
