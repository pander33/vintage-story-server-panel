using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Panel.Contracts;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Сериализация HTTP-ответа на языке запроса: <see cref="PanelJson.Options"/>, в которых
/// <see cref="Phrase"/> пишется переведённой строкой, а не объектом <c>$phrase</c>.
/// </summary>
/// <remarks>
/// Фразу переводят и там, где ответ собран заранее как <see cref="JsonNode"/>
/// (<c>SerializeToNode</c> с <see cref="PanelJson.Options"/>): объект с <c>$phrase</c> в дереве
/// пишется строкой. Поэтому обработчику язык знать не нужно — ответ собирается как раньше, а
/// выбор языка случается один раз, при записи.
/// </remarks>
public static class LocalizedJson
{
	private static readonly JsonSerializerOptions english = Create(PanelLanguage.English);
	private static readonly JsonSerializerOptions russian = Create(PanelLanguage.Russian);

	public static JsonSerializerOptions For(PanelLanguage language) =>
		language == PanelLanguage.Russian ? russian : english;

	/// <summary>
	/// Cookie с языком интерфейса. Её ставит фронтенд, и её видят и запросы <c>fetch</c>, и потоки
	/// <c>EventSource</c>, которым заголовок не задать. Без cookie — английский, как у интерфейса
	/// по умолчанию.
	/// </summary>
	public const string CookieName = "vs_lang";

	public static PanelLanguage LanguageOf(HttpContext http) =>
		PhraseCatalog.Parse(http.Request.Cookies.TryGetValue(CookieName, out var value) ? value : null);

	public static JsonSerializerOptions For(HttpContext http) => For(LanguageOf(http));

	/// <summary>
	/// Текст JSON для потока (<c>text/event-stream</c>), который пишется мимо <c>IResult</c>.
	/// <c>JsonNode.ToJsonString</c> здесь не годится: он пишет дерево мимо конвертеров, и фраза
	/// ушла бы объектом.
	/// </summary>
	public static string Serialize<T>(T value, HttpContext http) => JsonSerializer.Serialize(value, For(http));

	private static JsonSerializerOptions Create(PanelLanguage language)
	{
		var options = new JsonSerializerOptions(PanelJson.Options);
		options.Converters.Add(new TranslatedPhrase(language));
		options.Converters.Add(new TranslatedNodes(language));
		// Копия PanelJson.Options, которые ещё ни разу не сериализовали, приходит без резолвера типов.
		options.MakeReadOnly(populateMissingResolver: true);
		return options;
	}

	private sealed class TranslatedPhrase(PanelLanguage language) : JsonConverter<Phrase>
	{
		private static readonly PhraseJsonConverter neutral = new();

		public override Phrase Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
			neutral.Read(ref reader, typeToConvert, options);

		public override void Write(Utf8JsonWriter writer, Phrase value, JsonSerializerOptions options) =>
			writer.WriteStringValue(PhraseCatalog.Translate(value, language));
	}

	private sealed class TranslatedNodes(PanelLanguage language) : JsonConverterFactory
	{
		public override bool CanConvert(Type typeToConvert) => typeof(JsonNode).IsAssignableFrom(typeToConvert);

		public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
			(JsonConverter) Activator.CreateInstance(typeof(TranslatedNode<>).MakeGenericType(typeToConvert), language)!;
	}

	private sealed class TranslatedNode<T>(PanelLanguage language) : JsonConverter<T>
		where T : JsonNode
	{
		public override bool HandleNull => false;

		public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
			(T?) JsonNode.Parse(ref reader);

		public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
			WriteNode(writer, value, options);

		private void WriteNode(Utf8JsonWriter writer, JsonNode? node, JsonSerializerOptions options)
		{
			switch (node)
			{
				case null:
					writer.WriteNullValue();
					break;
				case JsonObject phrase when phrase[PhraseJsonConverter.TemplateName] is JsonValue template && template.GetValueKind() == JsonValueKind.String:
					writer.WriteStringValue(PhraseCatalog.Translate(phrase.Deserialize<Phrase>(PanelJson.Options)!, language));
					break;
				case JsonObject json:
					writer.WriteStartObject();
					foreach (var (name, value) in json)
					{
						writer.WritePropertyName(name);
						WriteNode(writer, value, options);
					}

					writer.WriteEndObject();
					break;
				case JsonArray array:
					writer.WriteStartArray();
					foreach (var item in array)
					{
						WriteNode(writer, item, options);
					}

					writer.WriteEndArray();
					break;
				default:
					node.WriteTo(writer, options);
					break;
			}
		}
	}
}
