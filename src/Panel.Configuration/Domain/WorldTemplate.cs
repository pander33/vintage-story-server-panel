using System.Text.Json.Nodes;
using Panel.SharedKernel;

namespace Panel.Configuration.Domain;

/// <summary>
/// <c>WorldConfig.WorldConfiguration</c> из <c>serverconfig.json</c>: шаблон, по которому игра
/// создаст новый мир.
/// </summary>
/// <remarks>
/// <para>
/// INV-20: шаблон и живая конфигурация работающего мира — разные вещи, хотя обе называются
/// «настройками мира». Живая приходит из телеметрии и меняется только разрешёнными командами
/// <c>/worldconfig</c> контекста Runtime.
/// </para>
/// <para>
/// Это два типа в двух сборках без преобразования между ними, поэтому записать шаблон в
/// работающий мир нельзя.
/// </para>
/// </remarks>
public sealed class WorldTemplate
{
	private const int maxDepth = 12;
	private const int maxMembers = 5000;
	private const int maxItems = 20_000;
	private const int maxKeyLength = 200;

	private readonly JsonObject document;

	private WorldTemplate(JsonObject document) => this.document = document;

	public static WorldTemplate Empty => new(new JsonObject());

	/// <summary>Копия содержимого.</summary>
	public JsonObject Document => (JsonObject) document.DeepClone();

	/// <summary>
	/// Шаблон из редактора. Проверяются глубина, число ключей и элементов, имена ключей. Типы
	/// значений не проверяются: параметры знает игра, а моды добавляют новые.
	/// </summary>
	public static Result<WorldTemplate> FromJson(JsonNode? node)
	{
		if (node is not JsonObject document)
		{
			return Failure.Invalid("WorldConfiguration должен быть JSON-объектом");
		}

		var safe = Inspect(document, 0);
		return safe.IsSuccess
			? new WorldTemplate((JsonObject) document.DeepClone())
			: Result.Fail<WorldTemplate>(safe.Failure!);
	}

	/// <summary>Шаблон, уже записанный в файле; проверки не нужны, его прочитала игра.</summary>
	internal static WorldTemplate Wrap(JsonObject document) => new(document);

	private static Result<Done> Inspect(JsonNode? node, int depth)
	{
		if (depth > maxDepth)
		{
			return Failure.Invalid("JSON имеет слишком большую вложенность");
		}

		switch (node)
		{
			case JsonObject map:
				if (map.Count > maxMembers)
				{
					return Failure.Invalid("Слишком много параметров");
				}

				foreach (var (key, value) in map)
				{
					if (key.EnumerateRunes().Count() > maxKeyLength || key.Any(symbol => symbol < ' '))
					{
						return Failure.Invalid("Недопустимое имя параметра");
					}

					var inner = Inspect(value, depth + 1);
					if (!inner.IsSuccess)
					{
						return inner;
					}
				}

				return Done.Ok;

			case JsonArray list:
				if (list.Count > maxItems)
				{
					return Failure.Invalid("Слишком большой массив");
				}

				foreach (var item in list)
				{
					var inner = Inspect(item, depth + 1);
					if (!inner.IsSuccess)
					{
						return inner;
					}
				}

				return Done.Ok;

			default:
				return Done.Ok;
		}
	}
}
