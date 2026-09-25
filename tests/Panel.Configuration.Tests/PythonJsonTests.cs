using System.Text.Json.Nodes;
using Panel.Configuration.Domain;

namespace Panel.Configuration.Tests;

/// <summary>
/// Текст JSON, как его пишет Python:
/// <c>json.dumps(json.loads(src), ensure_ascii=False, sort_keys=True, separators=(",", ":"))</c>.
/// </summary>
/// <remarks>
/// Ожидаемые строки выведены из исходников CPython (<c>format_float_short</c>, <c>ESCAPE_DCT</c>
/// в <c>json/encoder.py</c>), Python здесь не запускается.
///
/// От этой формы зависит ревизия в <c>config-apply.json</c> (ADR-04). Расхождение в одном
/// символе ничего не роняет, просто трекер перестаёт узнавать свою отметку, поэтому каждое
/// правило форматирования проверяется тестом.
/// </remarks>
public sealed class PythonJsonTests
{
	[Fact]
	public void Ключи_сортируются_кириллица_не_экранируется() =>
		Assert.Equal(
			"{\"a\":\"Привет\",\"b\":1,\"c\":[1,2.0,100000.0,null,true]}",
			PythonJson.Canonical(JsonNode.Parse("{\"c\":[1,2.0,1e5,null,true],\"b\":1,\"a\":\"Привет\"}")));

	[Theory]
	[InlineData("0.1", "0.1")]
	[InlineData("20.0", "20.0")]
	[InlineData("1e5", "100000.0")]
	[InlineData("1e15", "1000000000000000.0")]
	[InlineData("1e16", "1e+16")]
	[InlineData("0.0001", "0.0001")]
	[InlineData("0.00001", "1e-05")]
	[InlineData("1.5e-7", "1.5e-07")]
	[InlineData("-2.5", "-2.5")]
	[InlineData("123456789012345678.0", "1.2345678901234568e+17")]
	[InlineData("-0.0", "-0.0")]
	[InlineData("3.3333333333333335", "3.3333333333333335")]
	public void Дробное_печатается_как_repr(string source, string expected) =>
		Assert.Equal(expected, PythonJson.Canonical(JsonNode.Parse(source)));

	/// <summary>Целое остаётся целым любой длины: int в Python не ограничен.</summary>
	[Theory]
	[InlineData("42", "42")]
	[InlineData("-0", "0")]
	[InlineData("123456789012345678901234567890", "123456789012345678901234567890")]
	public void Целое_печатается_как_целое(string source, string expected) =>
		Assert.Equal(expected, PythonJson.Canonical(JsonNode.Parse(source)));

	[Fact]
	public void Управляющие_символы_экранируются_строчными_цифрами()
	{
		// U+2028 и DEL собираются из кодов: первый литералом компилятор считает переводом строки,
		// второй при чтении не виден.
		var separator = ((char) 0x2028).ToString();
		var delete = ((char) 0x7F).ToString();
		var escape = ((char) 0x1B).ToString();

		Assert.Equal(
			"\"a" + @"\u001bb\n\t\""\\" + separator + delete + "\"",
			PythonJson.Canonical(JsonValue.Create("a" + escape + "b\n\t\"\\" + separator + delete)));
	}

	/// <summary>Сравниваются кодовые точки, а не UTF-16: символ вне BMP идёт после U+FFFD.</summary>
	[Fact]
	public void Порядок_ключей_по_кодовым_точкам()
	{
		var astral = char.ConvertFromUtf32(0x1F600);
		var replacement = ((char) 0xFFFD).ToString();
		var map = new JsonObject { [astral] = 1, [replacement] = 2 };

		Assert.Equal("{\"" + replacement + "\":2,\"" + astral + "\":1}", PythonJson.Canonical(map));
	}

	[Fact]
	public void Отступ_как_indent_2() =>
		Assert.Equal(
			"{\n  \"z\": {\n    \"b\": [\n      1,\n      \"x\"\n    ]\n  },\n  \"e\": {},\n  \"l\": []\n}",
			PythonJson.Indented(JsonNode.Parse("{\"z\":{\"b\":[1,\"x\"]},\"e\":{},\"l\":[]}")));

	[Fact]
	public void Разделители_по_умолчанию() =>
		Assert.Equal(
			"{\"b\": [1, 2], \"a\": null}",
			PythonJson.Spaced(JsonNode.Parse("{\"b\":[1,2],\"a\":null}")));

	/// <summary>Границы в сообщениях проверки форматируются через <c>format(x, "g")</c>.</summary>
	[Theory]
	[InlineData(1, "1")]
	[InlineData(0, "0")]
	[InlineData(86_400, "86400")]
	[InlineData(604_800, "604800")]
	[InlineData(10_000_000, "1e+07")]
	[InlineData(3_600_000, "3.6e+06")]
	[InlineData(0.5, "0.5")]
	[InlineData(0.00001, "1e-05")]
	public void Общий_формат_как_g(double value, string expected) =>
		Assert.Equal(expected, PythonJson.FormatGeneral(value));

	/// <summary>SHA-256 от <c>{}</c>: единственный хеш, который проверяется полностью.</summary>
	[Fact]
	public void Ревизия_пустой_конфигурации() =>
		Assert.Equal(
			"44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",
			ConfigRevision.From(new JsonObject()).Value);

	[Fact]
	public void Ревизия_не_зависит_от_порядка_ключей_и_записи_чисел() =>
		Assert.Equal(
			ConfigRevision.From((JsonObject) JsonNode.Parse("{\"a\":1.0,\"b\":2}")!),
			ConfigRevision.From((JsonObject) JsonNode.Parse("{\"b\":2,\"a\":1.00}")!));
}
