using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Panel.Configuration.Domain;

/// <summary>
/// Текст JSON точно в том виде, в каком его пишет <c>json.dumps</c> в Python.
/// </summary>
/// <remarks>
/// <para>
/// Ревизия конфигурации — SHA-256 от
/// <c>json.dumps(config, ensure_ascii=False, sort_keys=True, separators=(",", ":"))</c>, и она
/// записана в <c>config-apply.json</c>, формат которого заморожен (ADR-04). Хеш, посчитанный
/// иначе, не совпадёт с записанным, и панель не узнает, что сохранённые настройки ждут
/// перезапуска.
/// </para>
/// <para>
/// <c>System.Text.Json</c> не подходит ни в одном режиме: он экранирует кириллицу (или
/// требует ослабленного кодировщика, который экранирует другое), не сортирует ключи и пишет
/// числа исходным текстом. Python разбирает <c>20.0</c> как float и печатает <c>20.0</c>, а
/// <c>1e5</c> печатает как <c>100000.0</c>.
/// </para>
/// </remarks>
public static class PythonJson
{
	private enum Layout
	{
		/// <summary><c>separators=(",", ":")</c> и <c>sort_keys=True</c>: для ревизии.</summary>
		Canonical,

		/// <summary>Разделители по умолчанию <c>", "</c> и <c>": "</c>: для оценки размера.</summary>
		Spaced,

		/// <summary><c>indent=2</c>: файл, который увидит администратор.</summary>
		Indented,
	}

	/// <summary>Каноническая форма, от которой считается ревизия.</summary>
	public static string Canonical(JsonNode? node) => Write(node, Layout.Canonical);

	/// <summary>Форма <c>json.dumps(value, ensure_ascii=False)</c> без параметров.</summary>
	public static string Spaced(JsonNode? node) => Write(node, Layout.Spaced);

	/// <summary>Форма <c>json.dumps(value, ensure_ascii=False, indent=2)</c>.</summary>
	public static string Indented(JsonNode? node) => Write(node, Layout.Indented);

	/// <summary>
	/// Представление числа с плавающей точкой, как его печатает <c>repr(float)</c>:
	/// кратчайшая запись, восстанавливающая то же значение, с экспонентой при
	/// позиции точки <c>≤ -4</c> или <c>&gt; 16</c>.
	/// </summary>
	public static string FloatRepr(double value)
	{
		if (double.IsNaN(value))
		{
			return "NaN";
		}

		if (double.IsInfinity(value))
		{
			return value > 0 ? "Infinity" : "-Infinity";
		}

		if (value == 0)
		{
			return double.IsNegative(value) ? "-0.0" : "0.0";
		}

		var (digits, point) = ShortestDigits(Math.Abs(value));
		var sign = value < 0 ? "-" : string.Empty;

		// format_float_short в CPython, режим 'r': экспонента при decpt <= -4 или decpt > 16.
		if (point <= -4 || point > 16)
		{
			var exponent = point - 1;
			var mantissa = digits.Length > 1 ? digits[..1] + "." + digits[1..] : digits;
			return string.Concat(
				sign,
				mantissa,
				exponent < 0 ? "e-" : "e+",
				Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture));
		}

		if (point <= 0)
		{
			return string.Concat(sign, "0.", new string('0', -point), digits);
		}

		return point >= digits.Length
			? string.Concat(sign, digits, new string('0', point - digits.Length), ".0")
			: string.Concat(sign, digits[..point], ".", digits[point..]);
	}

	/// <summary>
	/// <c>format(value, "g")</c>: шесть значащих цифр, экспонента вне диапазона <c>[-4, 6)</c>.
	/// Так форматируются границы в сообщениях проверки, поэтому предел DieAboveMemoryUsageMb
	/// печатается как <c>1e+07</c>, а не <c>10000000</c>.
	/// </summary>
	public static string FormatGeneral(double value)
	{
		if (value == 0)
		{
			return "0";
		}

		var scientific = value.ToString("E5", CultureInfo.InvariantCulture);
		var exponent = int.Parse(scientific[(scientific.IndexOf('E') + 1)..], CultureInfo.InvariantCulture);

		if (exponent < -4 || exponent >= 6)
		{
			var mantissa = TrimFraction(scientific[..scientific.IndexOf('E')]);
			return string.Concat(
				mantissa,
				exponent < 0 ? "e-" : "e+",
				Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture));
		}

		return TrimFraction(value.ToString("F" + (5 - exponent).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
	}

	/// <summary>
	/// Число без дробной части и экспоненты — <c>int</c> в Python, всё остальное, включая
	/// <c>20.0</c>, — <c>float</c>. Поле «целое» принимает только первое.
	/// </summary>
	public static bool IsIntegerToken(JsonValue value)
	{
		if (value.GetValueKind() != JsonValueKind.Number)
		{
			return false;
		}

		var raw = value.ToJsonString();
		return raw.IndexOfAny(['.', 'e', 'E']) < 0;
	}

	/// <summary>Сравнение строк по кодовым точкам, как <c>sorted()</c> в Python.</summary>
	private static int CompareCodePoints(string left, string right)
	{
		ArgumentNullException.ThrowIfNull(left);
		ArgumentNullException.ThrowIfNull(right);

		var a = left.EnumerateRunes();
		var b = right.EnumerateRunes();

		while (true)
		{
			var hasA = a.MoveNext();
			var hasB = b.MoveNext();

			if (!hasA || !hasB)
			{
				return hasA.CompareTo(hasB);
			}

			var order = a.Current.Value.CompareTo(b.Current.Value);
			if (order != 0)
			{
				return order;
			}
		}
	}

	private static string Write(JsonNode? node, Layout layout)
	{
		var builder = new StringBuilder();
		Append(builder, node, layout, 0);
		return builder.ToString();
	}

	private static void Append(StringBuilder builder, JsonNode? node, Layout layout, int depth)
	{
		switch (node)
		{
			case null:
				builder.Append("null");
				break;

			case JsonObject map:
				AppendObject(builder, map, layout, depth);
				break;

			case JsonArray list:
				AppendArray(builder, list, layout, depth);
				break;

			case JsonValue value:
				AppendValue(builder, value);
				break;
		}
	}

	private static void AppendObject(StringBuilder builder, JsonObject map, Layout layout, int depth)
	{
		if (map.Count == 0)
		{
			builder.Append("{}");
			return;
		}

		IEnumerable<KeyValuePair<string, JsonNode?>> members = map;
		if (layout == Layout.Canonical)
		{
			var sorted = map.ToList();
			sorted.Sort((x, y) => CompareCodePoints(x.Key, y.Key));
			members = sorted;
		}

		builder.Append('{');
		var first = true;
		foreach (var (key, item) in members)
		{
			Separate(builder, layout, depth + 1, first);
			first = false;

			AppendString(builder, key);
			builder.Append(layout == Layout.Canonical ? ":" : ": ");
			Append(builder, item, layout, depth + 1);
		}

		Close(builder, layout, depth, '}');
	}

	private static void AppendArray(StringBuilder builder, JsonArray list, Layout layout, int depth)
	{
		if (list.Count == 0)
		{
			builder.Append("[]");
			return;
		}

		builder.Append('[');
		var first = true;
		foreach (var item in list)
		{
			Separate(builder, layout, depth + 1, first);
			first = false;
			Append(builder, item, layout, depth + 1);
		}

		Close(builder, layout, depth, ']');
	}

	/// <summary>
	/// Разделитель перед элементом. С <c>indent</c> Python ставит <c>","</c> без пробела и перевод
	/// строки, без него — <c>", "</c> или <c>","</c>.
	/// </summary>
	private static void Separate(StringBuilder builder, Layout layout, int depth, bool first)
	{
		switch (layout)
		{
			case Layout.Indented:
				builder.Append(first ? "\n" : ",\n").Append(' ', depth * 2);
				break;

			case Layout.Spaced when !first:
				builder.Append(", ");
				break;

			case Layout.Canonical when !first:
				builder.Append(',');
				break;
		}
	}

	private static void Close(StringBuilder builder, Layout layout, int depth, char bracket)
	{
		if (layout == Layout.Indented)
		{
			builder.Append('\n').Append(' ', depth * 2);
		}

		builder.Append(bracket);
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
				builder.Append(Number(value));
				break;

			default:
				builder.Append("null");
				break;
		}
	}

	/// <summary>
	/// Число так, как его напечатает Python после <c>json.loads</c>: целое любой длины как целое,
	/// остальное как float через <c>repr</c>.
	/// </summary>
	private static string Number(JsonValue value)
	{
		var raw = value.ToJsonString();

		if (raw.IndexOfAny(['.', 'e', 'E']) >= 0)
		{
			return FloatRepr(double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture));
		}

		return BigInteger.Parse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
			.ToString(CultureInfo.InvariantCulture);
	}

	/// <summary>
	/// Строка при <c>ensure_ascii=False</c>: экранируются только кавычка, обратная косая черта и
	/// символы с кодом меньше пробела. Остальное, включая кириллицу и U+2028, пишется как есть.
	/// </summary>
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
				case '\n':
					builder.Append("\\n");
					break;
				case '\r':
					builder.Append("\\r");
					break;
				case '\t':
					builder.Append("\\t");
					break;
				case '\b':
					builder.Append("\\b");
					break;
				case '\f':
					builder.Append("\\f");
					break;
				case < ' ':
					// Python пишет шестнадцатеричные цифры строчными: \u001b, не \u001B.
					builder.Append("\\u").Append(((int) symbol).ToString("x4", CultureInfo.InvariantCulture));
					break;
				default:
					builder.Append(symbol);
					break;
			}
		}

		builder.Append('"');
	}

	/// <summary>
	/// Кратчайшие значащие цифры и позиция десятичной точки: значение равно
	/// <c>0.digits × 10^point</c>. Цифры берутся из форматирования "R": начиная с .NET Core 3.0
	/// оно соответствует IEEE и даёт те же цифры, что алгоритм Гея в CPython.
	/// </summary>
	private static (string Digits, int Point) ShortestDigits(double magnitude)
	{
		var text = magnitude.ToString("R", CultureInfo.InvariantCulture);
		var exponent = 0;

		var marker = text.IndexOfAny(['E', 'e']);
		if (marker >= 0)
		{
			exponent = int.Parse(text[(marker + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
			text = text[..marker];
		}

		var dot = text.IndexOf('.');
		var whole = dot >= 0 ? text[..dot] : text;
		var fraction = dot >= 0 ? text[(dot + 1)..] : string.Empty;

		var digits = whole + fraction;
		var point = whole.Length + exponent;

		var leading = 0;
		while (leading < digits.Length - 1 && digits[leading] == '0')
		{
			leading++;
		}

		digits = digits[leading..];
		point -= leading;

		var trimmed = digits.TrimEnd('0');
		return (trimmed.Length == 0 ? "0" : trimmed, point);
	}

	private static string TrimFraction(string text) =>
		text.Contains('.', StringComparison.Ordinal) ? text.TrimEnd('0').TrimEnd('.') : text;
}
