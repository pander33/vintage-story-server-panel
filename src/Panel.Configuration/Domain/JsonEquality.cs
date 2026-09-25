using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Panel.Configuration.Domain;

/// <summary>
/// Равенство значений JSON без учёта записи чисел.
/// </summary>
/// <remarks>
/// <c>1</c> и <c>1.0</c> равны, <c>true</c> равно <c>1</c>. Сравнение текстов JSON находило бы
/// изменения там, где их нет, например после переформатирования файла. Порядок ключей
/// объекта не важен, порядок элементов массива важен.
/// </remarks>
public static class JsonEquality
{
	public static bool AreEqual(JsonNode? left, JsonNode? right)
	{
		switch (left, right)
		{
			case (null, null):
				return true;

			case (null, _) or (_, null):
				return false;

			case (JsonObject a, JsonObject b):
				return a.Count == b.Count
					&& a.All(pair => b.TryGetPropertyValue(pair.Key, out var other) && AreEqual(pair.Value, other));

			case (JsonArray a, JsonArray b):
				return a.Count == b.Count && a.Zip(b).All(pair => AreEqual(pair.First, pair.Second));

			case (JsonValue a, JsonValue b):
				return ValuesEqual(a, b);

			default:
				return false;
		}
	}

	private static bool ValuesEqual(JsonValue left, JsonValue right)
	{
		var leftKind = left.GetValueKind();
		var rightKind = right.GetValueKind();

		if (leftKind == JsonValueKind.String || rightKind == JsonValueKind.String)
		{
			return leftKind == rightKind
				&& string.Equals(left.GetValue<string>(), right.GetValue<string>(), StringComparison.Ordinal);
		}

		if (leftKind == JsonValueKind.Null || rightKind == JsonValueKind.Null)
		{
			return leftKind == rightKind;
		}

		// Остались числа и логические значения, они сравниваются как числа.
		var a = Numeric(left, leftKind);
		var b = Numeric(right, rightKind);

		return a.Integer is { } x && b.Integer is { } y
			? x == y
			: a.Real.Equals(b.Real);
	}

	private static (BigInteger? Integer, double Real) Numeric(JsonValue value, JsonValueKind kind)
	{
		switch (kind)
		{
			case JsonValueKind.True:
				return (BigInteger.One, 1);

			case JsonValueKind.False:
				return (BigInteger.Zero, 0);
		}

		var raw = value.ToJsonString();
		if (PythonJson.IsIntegerToken(value))
		{
			var integer = BigInteger.Parse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
			return (integer, (double) integer);
		}

		return (null, double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture));
	}
}
