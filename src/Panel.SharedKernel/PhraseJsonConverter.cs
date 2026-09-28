using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Panel.SharedKernel;

/// <summary>
/// <see cref="Phrase"/> в JSON: <c>{"$phrase": "Порт {0} занят", "args": ["42420"]}</c>, значение —
/// строка или вложенная фраза той же формы.
/// </summary>
/// <remarks>
/// Так фраза лежит в файлах состояния и идёт по протоколу демона: язык выбирается только при
/// ответе на HTTP-запрос, и там её пишет строкой переводящий конвертер панели. Имя
/// <c>$phrase</c> не встречается в данных, поэтому фразу узнают и внутри готового
/// <c>JsonObject</c>. Читается и просто строка — так записаны тексты до появления фраз:
/// она становится готовым текстом без значений.
/// </remarks>
public sealed class PhraseJsonConverter : JsonConverter<Phrase>
{
	public const string TemplateName = "$phrase";
	public const string ArgsName = "args";

	private static readonly JsonSerializerOptions options = new();

	/// <summary>Фраза как узел — для ответов и записей, которые собираются как <see cref="JsonObject"/>.</summary>
	public static JsonNode ToNode(Phrase phrase) => JsonSerializer.SerializeToNode(phrase, options)!;

	/// <summary>Фраза из узла: объект <c>$phrase</c> или строка; <c>null</c> — если там ни то, ни другое.</summary>
	public static Phrase? FromNode(JsonNode? node) => node switch
	{
		JsonObject json when json[TemplateName] is JsonValue => json.Deserialize<Phrase>(options),
		JsonValue value when value.TryGetValue<string>(out var text) => Phrase.From(text),
		_ => null,
	};

	public override Phrase Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType == JsonTokenType.String)
		{
			return Phrase.From(reader.GetString()!);
		}

		if (reader.TokenType != JsonTokenType.StartObject)
		{
			throw new JsonException("Ожидалась фраза: строка или объект");
		}

		string? template = null;
		var args = new List<object>();
		while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
		{
			var name = reader.GetString();
			reader.Read();
			if (name == TemplateName)
			{
				template = reader.GetString();
			}
			else if (name == ArgsName && reader.TokenType == JsonTokenType.StartArray)
			{
				while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
				{
					args.Add(reader.TokenType == JsonTokenType.String ? reader.GetString()! : Read(ref reader, typeToConvert, options));
				}
			}
			else
			{
				reader.Skip();
			}
		}

		return new Phrase(template ?? throw new JsonException($"У фразы нет {TemplateName}"), args);
	}

	public override void Write(Utf8JsonWriter writer, Phrase value, JsonSerializerOptions options)
	{
		writer.WriteStartObject();
		writer.WriteString(TemplateName, value.Template);
		if (value.Args.Count > 0)
		{
			writer.WriteStartArray(ArgsName);
			foreach (var arg in value.Args)
			{
				if (arg is Phrase nested)
				{
					Write(writer, nested, options);
				}
				else
				{
					writer.WriteStringValue(arg.ToString());
				}
			}

			writer.WriteEndArray();
		}

		writer.WriteEndObject();
	}
}
