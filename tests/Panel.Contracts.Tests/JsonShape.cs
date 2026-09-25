using System.Text;
using System.Text.Json;

namespace Panel.Contracts.Tests;

/// <summary>
/// Сравнение формы двух JSON: набор ключей и тип каждого значения, без самих значений.
/// </summary>
/// <remarks>
/// Для золотых фикстур: pid, время и объём памяти меняются при каждом снятии, а набор полей и
/// их типы меняться не должны (ADR-16, только дополнения).
/// </remarks>
public static class JsonShape
{
	public static void AssertSameShape(string expected, string actual)
	{
		using var left = JsonDocument.Parse(expected);
		using var right = JsonDocument.Parse(actual);

		var differences = new List<string>();
		Compare("$", left.RootElement, right.RootElement, differences);

		if (differences.Count > 0)
		{
			var message = new StringBuilder("Форма ответа разошлась с фикстурой:");
			foreach (var difference in differences)
			{
				message.Append("\n  ").Append(difference);
			}

			throw new Xunit.Sdk.XunitException(message.ToString());
		}
	}

	private static void Compare(string path, JsonElement expected, JsonElement actual, List<string> differences)
	{
		if (!SameKind(expected, actual))
		{
			differences.Add($"{path}: ожидался {expected.ValueKind}, получен {actual.ValueKind}");
			return;
		}

		switch (expected.ValueKind)
		{
			case JsonValueKind.Object:
				foreach (var property in expected.EnumerateObject())
				{
					if (!actual.TryGetProperty(property.Name, out var value))
					{
						differences.Add($"{path}.{property.Name}: поле отсутствует");
						continue;
					}

					Compare($"{path}.{property.Name}", property.Value, value, differences);
				}

				// Лишние поля допустимы: ADR-16 разрешает добавлять поля, но не переименовывать и не
				// удалять. Фронтенд незнакомые поля игнорирует.
				break;

			case JsonValueKind.Array:
				var first = expected.EnumerateArray().FirstOrDefault();
				if (first.ValueKind is not JsonValueKind.Undefined)
				{
					var index = 0;
					foreach (var item in actual.EnumerateArray())
					{
						Compare($"{path}[{index++}]", first, item, differences);
					}
				}

				break;
		}
	}

	/// <summary>
	/// true и false — один вид значения, 1 и 1.0 тоже: JSON не различает целые и дробные, а
	/// started_at бывает и тем, и другим.
	/// </summary>
	private static bool SameKind(JsonElement expected, JsonElement actual)
	{
		if (expected.ValueKind == actual.ValueKind)
		{
			return true;
		}

		var booleans = new[] { JsonValueKind.True, JsonValueKind.False };
		if (booleans.Contains(expected.ValueKind) && booleans.Contains(actual.ValueKind))
		{
			return true;
		}

		// null допустим только там, где он был и в фикстуре.
		return false;
	}
}
