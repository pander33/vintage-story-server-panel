using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Panel.Contracts;
using Panel.Contracts.Http;
using Panel.SharedKernel;
using Panel.Web.Application;

namespace Panel.Contracts.Tests;

public sealed class GeneratedJsonTests
{
	[Theory]
	[InlineData(null)]
	[InlineData(PanelLanguage.English)]
	[InlineData(PanelLanguage.Russian)]
	public void Метаданные_сохраняют_форму_числа_и_перевод_вложенных_фраз(PanelLanguage? language)
	{
		var options = language is { } selected ? LocalizedJson.For(selected) : PanelJson.Options;
		var reflection = new JsonSerializerOptions(options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
		var response = new TelemetryResponse
		{
			Current = new JsonObject
			{
				["available"] = true,
				["detail"] = PhraseJsonConverter.ToNode(Phrase.From("Проверка ещё не выполнялась")),
				["optional"] = null,
				["player"] = "Игрок ё",
			},
			History = [new PingPointResponse { Ts = 1_800_000_000, PingMilliseconds = 12.5 }],
			Interval = 2,
		};

		var json = JsonSerializer.Serialize(response, options);
		Assert.Equal(JsonSerializer.Serialize(response, reflection), json);
		var read = JsonSerializer.Deserialize<TelemetryResponse>(json, options)!;
		Assert.Equal(response.History, read.History);
		Assert.Equal(response.Interval, read.Interval);
		Assert.Equal("Игрок ё", read.Current["player"]!.GetValue<string>());
		Assert.Equal(language is null, json.Contains("$phrase", StringComparison.Ordinal));
	}

	[Fact]
	public void Незарегистрированный_тип_сериализуется_через_резервный_резолвер()
	{
		var value = new { optional = (int?) null, text = "ё", count = 2 };
		Assert.Equal("{\"optional\":null,\"text\":\"ё\",\"count\":2}", JsonSerializer.Serialize(value, PanelJson.Options));
	}
}
