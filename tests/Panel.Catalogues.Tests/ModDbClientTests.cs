using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Panel.Catalogues.Domain;
using Panel.Catalogues.Infrastructure;

namespace Panel.Catalogues.Tests;

/// <summary>Клиент ModDB на подменённом HTTP.</summary>
public sealed class ModDbClientTests
{
	private static readonly GameVersion game = GameVersion.Create("1.22.7");

	[Fact]
	public async Task Детали_принимают_ту_же_ветку_и_чистят_changelog()
	{
		var client = Client(new()
		{
			["/api/mod/example"] = Mod(
				Release(1, "2.0", ["1.22.7"]) with { Changelog = "<p>Fixed &amp; <b>faster</b></p>" },
				Release(2, "1.0", ["1.22.6"]),
				Release(3, "0.9", ["1.21.7"])),
		});

		var value = (await client.DetailsAsync("example", game, string.Empty, default)).Value;
		var releases = value.Json["releases"]!.AsArray();

		Assert.Equal("exact", releases[0]!["compatibility"]!.GetValue<string>());
		Assert.Equal("Fixed & faster", releases[0]!["changelog"]!.GetValue<string>());
		Assert.False(releases[0]!["prerelease"]!.GetValue<bool>());
		Assert.Equal("same_minor", releases[1]!["compatibility"]!.GetValue<string>());
		Assert.Equal("1.22.6", releases[1]!["compatible_tag"]!.GetValue<string>());
		Assert.False(releases[2]!["compatible"]!.GetValue<bool>());
		Assert.Null(releases[2]!["compatibility"]);
		Assert.Equal("example", value.LookupIdentifier);
		Assert.Equal(
			["modid", "name", "author", "urlalias", "side", "homepageurl", "releases", "game_version", "lookup_identifier"],
			value.Json.Select(pair => pair.Key));
	}

	[Fact]
	public async Task Скачивание_отклоняет_чужой_хост()
	{
		var client = Client(new() { ["/api/mod/example"] = Mod(Release(7, "1.0", ["1.22.7"], "https://example.com/evil.zip")) });

		var download = await client.ResolveDownloadAsync("example", 7, game, default);

		Assert.Equal("ModDB вернул недопустимый адрес загрузки", download.Failure!.Message);
	}

	[Fact]
	public async Task Скачивание_заново_проверяет_совместимость()
	{
		var client = Client(new() { ["/api/mod/example"] = Mod(Release(7, "1.0", ["1.21.7"])) });

		var download = await client.ResolveDownloadAsync("example", 7, game, default);

		Assert.Equal("Релиз не отмечен совместимым с Vintage Story 1.22.7", download.Failure!.Message);
	}

	[Fact]
	public async Task Скачивание_принимает_релиз_той_же_ветки()
	{
		var client = Client(new() { ["/api/mod/example"] = Mod(Release(7, "1.0", ["1.22.6"]) with { FileName = "mod.zip" }) });

		var download = (await client.ResolveDownloadAsync("example", 7, game, default)).Value;

		Assert.Equal("https://moddbcdn.vintagestory.at/mod.zip", download.Url.OriginalString);
		Assert.Equal("mod.zip", download.FileName);
	}

	[Fact]
	public async Task Неизвестный_релиз_не_найден()
	{
		var client = Client(new() { ["/api/mod/example"] = Mod(Release(7, "1.0", ["1.22.7"])) });

		Assert.Equal("Релиз не найден", (await client.ResolveDownloadAsync("example", 8, game, default)).Failure!.Message);
	}

	[Fact]
	public async Task Битый_modid_открывается_по_номеру_страницы_из_поиска()
	{
		var client = Client(new()
		{
			["/api/mods"] = new JsonObject
			{
				["mods"] = new JsonArray(new JsonObject { ["modid"] = 11556, ["name"] = "Newfies Server Essentials Suite" }),
			},
			["/api/mod/11556"] = new JsonObject
			{
				["mod"] = new JsonObject { ["name"] = "Newfies Server Essentials Suite", ["releases"] = new JsonArray() },
			},
		});

		var value = await client.DetailsAsync(
			"newfiesserveressentialssuite", game, "Newfies Server Essentials Suite", default);

		Assert.Equal("11556", value.Value.LookupIdentifier);
	}

	[Fact]
	public async Task Не_найденный_и_поиском_мод_даёт_исходную_ошибку()
	{
		var client = Client(new() { ["/api/mods"] = new JsonObject { ["mods"] = new JsonArray() } });

		var value = await client.DetailsAsync("broken", game, string.Empty, default);

		Assert.Equal("ModDB недоступен: HTTP Error 404: Not Found", value.Failure!.Message);
	}

	[Fact]
	public async Task Страница_без_релизов_отличается_от_предрелизов()
	{
		var empty = Client(new() { ["/api/mod/67501"] = Mod() });
		var candidate = (await empty.CompatibleReleaseAsync("67501", game, false, string.Empty, default)).Value;
		Assert.Null(candidate.Release);
		Assert.Equal("no_releases", candidate.Lookup.Reason);

		var prerelease = Client(new() { ["/api/mod/example"] = Mod(Release(1, "2.0.0-rc.1", ["1.22.7"])) });
		Assert.Equal(
			"prerelease_only",
			(await prerelease.CompatibleReleaseAsync("example", game, false, string.Empty, default)).Value.Lookup.Reason);
	}

	[Fact]
	public async Task Найденный_релиз_несёт_идентификатор_для_скачивания()
	{
		var client = Client(new() { ["/api/mod/example"] = Mod(Release(1, "2.2.0", ["1.22.7"])) });

		var release = (await client.CompatibleReleaseAsync("example", game, false, string.Empty, default)).Value.Release!;

		Assert.Equal("example", release["identifier"]!.GetValue<string>());
		Assert.Equal("identifier", release.Last().Key);
	}

	[Theory]
	[InlineData("x")]
	[InlineData(" y ")]
	public async Task Поиск_требует_от_двух_до_восьмидесяти_символов(string text)
	{
		var result = await Client([]).SearchAsync(text, default);

		Assert.Equal("Введите от 2 до 80 символов", result.Failure!.Message);
	}

	[Fact]
	public async Task Отклонённый_statuscode_это_отказ()
	{
		var client = Client(new() { ["/api/mod/example"] = new JsonObject { ["statuscode"] = "404" } });

		// Через скачивание: для деталей отказ ушёл бы в запасной поиск.
		Assert.Equal("ModDB отклонил запрос", (await client.ResolveDownloadAsync("example", 1, game, default)).Failure!.Message);
	}

	[Fact]
	public async Task Идентификатор_проверяется_до_запроса()
	{
		Assert.Equal(
			"Некорректный идентификатор мода",
			(await Client([]).DetailsAsync("../etc", game, "", default)).Failure!.Message);
	}

	[Fact]
	public async Task Архив_больше_предела_не_скачивается()
	{
		var client = new ModDbClient(new HttpClient(new Handler(_ =>
			new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) })));

		var data = await client.DownloadAsync(new Uri("https://moddbcdn.vintagestory.at/mod.zip"), 1024, default);

		Assert.Equal("Архив мода превышает лимит панели", data.Failure!.Message);
		Assert.Equal(
			"Недопустимый адрес загрузки",
			(await client.DownloadAsync(new Uri("http://moddbcdn.vintagestory.at/mod.zip"), 1024, default)).Failure!.Message);
	}

	[Fact]
	public void Html_вычищается_в_текст()
	{
		// </li> и <BR/> дают по переносу, пустая строка между ними остаётся.
		Assert.Equal("a\nb\n\nc", ModDbClient.StripHtml("<p>a</p><ul><li>b</li></ul><BR/>c"));
		Assert.Equal("\"<x>\"", ModDbClient.StripHtml("&quot;&lt;x&gt;&quot;"));
	}

	private sealed record ReleaseSpec(long Id, string Version, string[] Tags, string MainFile)
	{
		public string? Changelog { get; init; }

		public string? FileName { get; init; }

		public JsonObject ToJson() => new()
		{
			["releaseid"] = Id,
			["modversion"] = Version,
			["tags"] = new JsonArray([.. Tags.Select(tag => (JsonNode) tag)]),
			["mainfile"] = MainFile,
			["filename"] = FileName,
			["changelog"] = Changelog,
		};
	}

	private static ReleaseSpec Release(long id, string version, string[] tags, string mainFile = "https://moddbcdn.vintagestory.at/mod.zip") =>
		new(id, version, tags, mainFile);

	private static JsonObject Mod(params ReleaseSpec[] releases) => new()
	{
		["mod"] = new JsonObject
		{
			["name"] = "Example",
			["releases"] = new JsonArray([.. releases.Select(release => (JsonNode) release.ToJson())]),
		},
	};

	/// <summary>Ответы по пути; на остальное 404, как у ModDB для неизвестного мода.</summary>
	private static ModDbClient Client(Dictionary<string, JsonObject> responses) =>
		new(new HttpClient(new Handler(request =>
			responses.TryGetValue(request.RequestUri!.AbsolutePath, out var body)
				? new HttpResponseMessage(HttpStatusCode.OK)
				{
					Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
				}
				: new HttpResponseMessage(HttpStatusCode.NotFound))));

	private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(respond(request));
	}
}
