using System.Text.Json.Nodes;
using Panel.Configuration.Domain;

namespace Panel.Configuration.Tests;

/// <summary>
/// Каноническая форма ревизии настроек: одинаковые по смыслу документы дают один текст.
/// </summary>
public sealed class CanonicalJsonTests
{
	[Fact]
	public void Ключи_по_порядку_без_пробелов_кириллица_как_есть() =>
		Assert.Equal(
			"{\"a\":\"Привет\",\"b\":1,\"c\":[1,2,100000,null,true]}",
			CanonicalJson.Write(JsonNode.Parse("{\"c\":[1,2.0,1e5,null,true],\"b\":1,\"a\":\"Привет\"}")));

	[Theory]
	[InlineData("20", "20")]
	[InlineData("20.0", "20")]
	[InlineData("2e1", "20")]
	[InlineData("-0.0", "0")]
	[InlineData("-0", "0")]
	[InlineData("0.1", "1e-1")]
	[InlineData("0.10", "1e-1")]
	[InlineData("1.5e-7", "15e-8")]
	[InlineData("-2.50E+3", "-2500")]
	[InlineData("123456789012345678901234567890", "123456789012345678901234567890")]
	[InlineData("100000000000000000000", "100000000000000000000")]
	[InlineData("1e20", "100000000000000000000")]
	[InlineData("1e300", "1e300")]
	[InlineData("1e400", "1e400")]
	[InlineData("1e999999999999", "1e999999999999")]
	[InlineData("12345678901234567890123456789012345e-35", "12345678901234567890123456789012345e-35")]
	public void Число_по_значению(string source, string expected) =>
		Assert.Equal(expected, CanonicalJson.Write(JsonNode.Parse(source)));

	[Fact]
	public void Экранируются_только_кавычка_косая_и_управляющие()
	{
		var escape = ((char) 0x1B).ToString();
		var separator = ((char) 0x2028).ToString();

		Assert.Equal(
			"\"a\\u001bb\\u000a\\\"\\\\" + separator + "\"",
			CanonicalJson.Write(JsonValue.Create("a" + escape + "b\n\"\\" + separator)));
	}

	/// <summary>SHA-256 от <c>{}</c> — такая же, как была у прежней формы.</summary>
	[Fact]
	public void Ревизия_пустой_конфигурации() =>
		Assert.Equal(
			"44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",
			ConfigRevision.From(new JsonObject()).Value);

	/// <summary>Игра переписывает свой файл при старте; запись чисел и порядок ключей ревизию не меняют.</summary>
	[Fact]
	public void Ревизия_не_зависит_от_порядка_ключей_и_записи_чисел() =>
		Assert.Equal(
			ConfigRevision.From((JsonObject) JsonNode.Parse("{\"a\":1,\"b\":{\"y\":2.50,\"x\":[3.0]}}")!),
			ConfigRevision.From((JsonObject) JsonNode.Parse("{\"b\":{\"x\":[3],\"y\":2.5},\"a\":1.00}")!));

	[Fact]
	public void Ревизия_меняется_со_значением() =>
		Assert.NotEqual(
			ConfigRevision.From((JsonObject) JsonNode.Parse("{\"Port\":42420}")!),
			ConfigRevision.From((JsonObject) JsonNode.Parse("{\"Port\":42421}")!));
}
