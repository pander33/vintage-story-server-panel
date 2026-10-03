using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Panel.Configuration.Domain;

/// <summary>
/// Каноническая форма документа, от которой считается ревизия настроек.
/// </summary>
/// <remarks>
/// <para>
/// Форма своя и не повторяет ни <c>json.dumps</c> Python, ни <c>System.Text.Json</c>: от неё
/// требуется только, чтобы одинаковые по смыслу документы давали одинаковый текст. Ключи
/// объекта упорядочены по кодам UTF-16, разделителей без пробелов, строки экранируют только
/// кавычку, обратную косую черту и управляющие символы.
/// </para>
/// <para>
/// Число записывается по точному десятичному значению, а не по исходному тексту: <c>20</c>,
/// <c>20.0</c> и <c>2e1</c> — одно и то же, <c>1e20</c> и <c>100000000000000000000</c> тоже. Игра
/// при старте переписывает свой файл, и запись числа в нём не должна менять ревизию. Через
/// <see cref="double"/> число не проходит: большие целые потеряли бы точность или ушли бы в
/// бесконечность, и равные значения дали бы разный текст, а разные — одинаковый.
/// </para>
/// </remarks>
public static class CanonicalJson
{
	/// <summary>До скольких нулей целое пишется цифрами; дальше — показателем, чтобы <c>1e999999999</c> не разворачивался в память.</summary>
	private const int maxTrailingZeros = 32;

	public static string Write(JsonNode? node)
	{
		var builder = new StringBuilder();
		Append(builder, node);
		return builder.ToString();
	}

	private static void Append(StringBuilder builder, JsonNode? node)
	{
		switch (node)
		{
			case JsonObject map:
				var members = map.ToList();
				members.Sort((x, y) => string.CompareOrdinal(x.Key, y.Key));
				builder.Append('{');
				for (var index = 0; index < members.Count; index++)
				{
					if (index > 0)
					{
						builder.Append(',');
					}

					AppendString(builder, members[index].Key);
					builder.Append(':');
					Append(builder, members[index].Value);
				}

				builder.Append('}');
				break;

			case JsonArray list:
				builder.Append('[');
				for (var index = 0; index < list.Count; index++)
				{
					if (index > 0)
					{
						builder.Append(',');
					}

					Append(builder, list[index]);
				}

				builder.Append(']');
				break;

			case JsonValue value:
				AppendValue(builder, value);
				break;

			default:
				builder.Append("null");
				break;
		}
	}

	private static void AppendValue(StringBuilder builder, JsonValue value)
	{
		switch (value.GetValueKind())
		{
			case JsonValueKind.String:
				AppendString(builder, value.GetValue<string>());
				break;

			case JsonValueKind.True:
				builder.Append("true");
				break;

			case JsonValueKind.False:
				builder.Append("false");
				break;

			case JsonValueKind.Number:
				builder.Append(Number(value.ToJsonString()));
				break;

			default:
				builder.Append("null");
				break;
		}
	}

	/// <summary>
	/// Значащие цифры без нулей по краям и показатель: <c>2.50e3</c> — цифры <c>25</c> и показатель
	/// <c>2</c>, то есть <c>2500</c>. Целое с не более чем <see cref="maxTrailingZeros"/> нулями
	/// пишется цифрами, остальное — <c>&lt;цифры&gt;e&lt;показатель&gt;</c>. Значащие цифры не
	/// кончаются нулём, поэтому запись однозначна. Ноль — <c>0</c> при любом знаке.
	/// </summary>
	private static string Number(string raw)
	{
		var text = raw.AsSpan();
		var negative = text[0] == '-';
		if (negative)
		{
			text = text[1..];
		}

		var exponent = BigInteger.Zero;
		var marker = text.IndexOfAny('e', 'E');
		if (marker >= 0)
		{
			exponent = BigInteger.Parse(text[(marker + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
			text = text[..marker];
		}

		var point = text.IndexOf('.');
		var digits = point < 0 ? text.ToString() : string.Concat(text[..point], text[(point + 1)..]);
		if (point >= 0)
		{
			exponent -= text.Length - point - 1;
		}

		digits = digits.TrimStart('0');
		if (digits.Length == 0)
		{
			return "0";
		}

		var significant = digits.TrimEnd('0');
		exponent += digits.Length - significant.Length;

		var sign = negative ? "-" : string.Empty;
		return exponent >= 0 && exponent <= maxTrailingZeros
			? sign + significant + new string('0', (int) exponent)
			: sign + significant + "e" + exponent.ToString(CultureInfo.InvariantCulture);
	}

	private static void AppendString(StringBuilder builder, string value)
	{
		builder.Append('"');
		foreach (var symbol in value)
		{
			switch (symbol)
			{
				case '"':
					builder.Append("\\\"");
					break;
				case '\\':
					builder.Append("\\\\");
					break;
				case < ' ':
					builder.Append("\\u").Append(((int) symbol).ToString("x4", CultureInfo.InvariantCulture));
					break;
				default:
					builder.Append(symbol);
					break;
			}
		}

		builder.Append('"');
	}
}
