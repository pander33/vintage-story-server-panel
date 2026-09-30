using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Panel.Catalogues.Domain;
using Panel.Catalogues.Infrastructure;

namespace Panel.Catalogues.Tests;

/// <summary>Каталог модов: отбор и порядок, кэш списка, прокси картинок.</summary>
public sealed class ModCatalogueTests
{
	private static readonly GameVersion game = GameVersion.Create("1.22.7");

	private static CatalogueEntry Entry(
		long id, string name, string author = "Автор", string side = "both", string[]? tags = null,
		long downloads = 0, long follows = 0, long trending = 0, string released = "2026-01-01 00:00:00") =>
		new(id, [$"mod{id}"], name, "Описание", author, side, tags ?? [], downloads, follows, trending, released, null, null);

	private static List<CatalogueEntry> Twenty() =>
	[
		.. Enumerable.Range(1, 20).Select(index => Entry(
			index,
			$"Мод {index}",
			author: index % 2 == 0 ? "Чётный" : "Нечётный",
			side: index % 3 == 0 ? "server" : "both",
			tags: index % 5 == 0 ? ["QoL", "Storage"] : ["QoL"],
			downloads: index * 100,
			follows: 20 - index,
			trending: index % 4,
			released: $"2026-01-{index:00} 00:00:00")),
	];

	[Fact]
	public void По_умолчанию_порядок_по_скачиваниям_и_страница_ограничена()
	{
		var page = ModCatalogue.Page(Twenty(), CatalogueQuery.ParseParameters(null, [], null, false, null, 0, 5), null);

		Assert.Equal(20, page.Total);
		Assert.Equal([20L, 19, 18, 17, 16], page.Items.Select(entry => entry.ModId));
	}

	[Theory]
	[InlineData("follows", 1)]
	[InlineData("updated", 20)]
	[InlineData("downloads", 20)]
	public void Порядок_выбирается_параметром(string sort, long first)
	{
		var page = ModCatalogue.Page(Twenty(), CatalogueQuery.ParseParameters(sort, [], null, false, null, 0, 3), null);

		Assert.Equal(first, page.Items[0].ModId);
	}

	[Fact]
	public void Неизвестный_порядок_и_страница_приводятся_к_пределам()
	{
		var query = CatalogueQuery.ParseParameters("nonsense", [], "wat", false, new string('я', 200), -5, 9999);

		Assert.Equal(CatalogueSort.Downloads, query.Sort);
		Assert.Equal(string.Empty, query.Side);
		Assert.Equal(80, query.Text.Length);
		Assert.Equal(0, query.Offset);
		Assert.Equal(CatalogueQuery.MaxLimit, query.Limit);
	}

	[Fact]
	public void Теги_сторона_и_текст_сужают_выбор()
	{
		var tags = ModCatalogue.Page(Twenty(), CatalogueQuery.ParseParameters(null, ["storage"], null, false, null, 0, 60), null);
		var side = ModCatalogue.Page(Twenty(), CatalogueQuery.ParseParameters(null, [], "server", false, null, 0, 60), null);
		var text = ModCatalogue.Page(Twenty(), CatalogueQuery.ParseParameters(null, [], null, false, "нечёт", 0, 60), null);

		Assert.Equal([5L, 10, 15, 20], tags.Items.Select(entry => entry.ModId).Order());
		Assert.Equal(6, side.Total);
		Assert.Equal(10, text.Total);
	}

	[Fact]
	public void Только_совместимые_отбирает_по_известному_набору_и_не_режет_при_неизвестном()
	{
		var query = CatalogueQuery.ParseParameters(null, [], null, true, null, 0, 60);

		var known = ModCatalogue.Page(Twenty(), query, new HashSet<long> { 3, 7 });
		var unknown = ModCatalogue.Page(Twenty(), query, null);

		Assert.Equal([7L, 3], known.Items.Select(entry => entry.ModId));
		Assert.Equal(20, unknown.Total);
	}

	/// <summary>
	/// Установленные убираются на стороне панели, иначе страница с отбором в браузере была бы короче
	/// запрошенной, а «всего» — неверным. Без списка установленных отбор не действует.
	/// </summary>
	[Fact]
	public void Скрыть_установленные_по_строковому_идентификатору_без_учёта_регистра()
	{
		var query = CatalogueQuery.ParseParameters(null, [], null, false, null, 0, 60) with { HideInstalled = true };
		var installed = new HashSet<string>(StringComparer.Ordinal) { "mod20", "mod19" };

		var hidden = ModCatalogue.Page(Twenty(), query, null, installed);
		var unknown = ModCatalogue.Page(Twenty(), query, null, null);
		var shown = ModCatalogue.Page(Twenty(), query with { HideInstalled = false }, null, installed);

		Assert.Equal(18, hidden.Total);
		Assert.DoesNotContain(hidden.Items, entry => entry.ModId is 19 or 20);
		Assert.Equal(20, unknown.Total);
		Assert.Equal(20, shown.Total);
		Assert.True((Entry(1, "Мод") with { Identifiers = ["MyMod"] }).IsAmong(new HashSet<string> { "mymod" }));
	}

	[Fact]
	public void Страница_со_смещением_продолжает_список()
	{
		var second = ModCatalogue.Page(Twenty(), CatalogueQuery.ParseParameters(null, [], null, false, null, 5, 5), null);

		Assert.Equal([15L, 14, 13, 12, 11], second.Items.Select(entry => entry.ModId));
	}

	[Fact]
	public void Запись_другого_типа_в_каталог_не_попадает()
	{
		Assert.Null(CatalogueEntry.From(new JsonObject { ["modid"] = 1, ["type"] = "other" }));
		Assert.Null(CatalogueEntry.From(new JsonObject { ["type"] = "mod" }));
		Assert.Equal(7, CatalogueEntry.From(new JsonObject { ["modid"] = 7, ["type"] = "mod", ["urlalias"] = "seven" })!.ModId);
		Assert.Equal("seven", CatalogueEntry.From(new JsonObject { ["modid"] = 7, ["urlalias"] = "seven" })!.LookupKey);
	}

	[Fact]
	public void Теги_по_частоте_без_неиспользуемых()
	{
		var tags = new List<CatalogueTag> { new("Unused", "#000"), new("Storage", "#111"), new("QoL", "#222") };

		var popular = ModCatalogue.PopularTags(Twenty(), tags);

		Assert.Equal(["QoL", "Storage"], popular.Select(item => item.Tag.Name));
		Assert.Equal([20, 4], popular.Select(item => item.Count));
	}

	// ---------- кэш ----------

	[Fact]
	public async Task Параллельные_запросы_ждут_одну_загрузку()
	{
		var counter = new Counter();
		var cache = new ModCatalogueCache(Client(counter, catalogue: Listing(3)), TimeProvider.System);

		var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => cache.SnapshotAsync(game, default)));

		Assert.All(results, result => Assert.True(result.IsSuccess));
		Assert.Equal(1, counter.Of("/api/mods", "gameversions"));
		Assert.Equal(1, counter.Of("/api/mods", string.Empty));
	}

	[Fact]
	public async Task Совместимость_берётся_по_метке_версии_игры()
	{
		var cache = new ModCatalogueCache(Client(new Counter(), catalogue: Listing(3), compatible: [2]), TimeProvider.System);

		var snapshot = (await cache.SnapshotAsync(game, default)).Value;

		Assert.Equal([2L], snapshot.Compatible!);
		Assert.Equal("QoL", snapshot.Tags[0].Name);
	}

	[Fact]
	public async Task Версии_нет_в_списке_ModDB_даёт_пустой_набор()
	{
		var cache = new ModCatalogueCache(Client(new Counter(), catalogue: Listing(2)), TimeProvider.System);

		var snapshot = (await cache.SnapshotAsync(GameVersion.Create("9.9.9"), default)).Value;

		Assert.Empty(snapshot.Compatible!);
	}

	[Fact]
	public async Task Отказ_ModDB_без_кэша_это_отказ_а_с_кэшем_старый_список()
	{
		var clock = new ManualClock();
		var up = true;
		var cache = new ModCatalogueCache(Client(new Counter(), catalogue: Listing(3), available: () => up), clock);

		var first = await cache.SnapshotAsync(game, default);
		clock.Advance(ModCatalogueCache.Lifetime + TimeSpan.FromMinutes(1));
		up = false;
		var stale = await cache.SnapshotAsync(game, default);

		Assert.Equal(first.Value.Entries.Count, stale.Value.Entries.Count);
		Assert.True(stale.Value.Stale);
		Assert.False(first.Value.Stale);

		var fresh = new ModCatalogueCache(Client(new Counter(), catalogue: Listing(3), available: () => false), TimeProvider.System);
		Assert.False((await fresh.SnapshotAsync(game, default)).IsSuccess);
	}

	[Fact]
	public async Task После_отказа_ModDB_повтор_только_после_паузы()
	{
		var clock = new ManualClock();
		var counter = new Counter();
		var up = false;
		var cache = new ModCatalogueCache(Client(counter, catalogue: Listing(3), available: () => up), clock);

		Assert.False((await cache.SnapshotAsync(game, default)).IsSuccess);
		Assert.False((await cache.SnapshotAsync(game, default)).IsSuccess);
		Assert.Equal(1, counter.Of("/api/mods", string.Empty));

		up = true;
		clock.Advance(ModCatalogueCache.RetryDelay + TimeSpan.FromSeconds(1));
		Assert.True((await cache.SnapshotAsync(game, default)).IsSuccess);
		Assert.Equal(2, counter.Of("/api/mods", string.Empty));
	}

	[Fact]
	public async Task Устаревший_список_при_недоступном_ModDB_не_перезапрашивается_каждый_раз()
	{
		var clock = new ManualClock();
		var counter = new Counter();
		var up = true;
		var cache = new ModCatalogueCache(Client(counter, catalogue: Listing(3), available: () => up), clock);

		await cache.SnapshotAsync(game, default);
		up = false;
		clock.Advance(ModCatalogueCache.Lifetime + TimeSpan.FromMinutes(1));
		var first = await cache.SnapshotAsync(game, default);
		var second = await cache.SnapshotAsync(game, default);

		Assert.True(first.Value.Stale);
		Assert.True(second.Value.Stale);
		Assert.Equal(2, counter.Of("/api/mods", string.Empty));
	}

	[Fact]
	public async Task Свежий_список_повторно_не_запрашивается()
	{
		var counter = new Counter();
		var cache = new ModCatalogueCache(Client(counter, catalogue: Listing(3)), TimeProvider.System);

		await cache.SnapshotAsync(game, default);
		await cache.SnapshotAsync(game, default);

		Assert.Equal(1, counter.Of("/api/mods", string.Empty));
	}

	// ---------- прокси картинок ----------

	private static readonly byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

	private const string Cdn = "https://moddbcdn.vintagestory.at/logo.png";

	[Fact]
	public async Task Картинка_отдаётся_с_типом_по_первым_байтам_и_кэшируется()
	{
		var calls = 0;
		var proxy = Proxy(_ => { calls++; return Image(png, "image/png"); });

		var first = await proxy.GetAsync(Cdn, default);
		var second = await proxy.GetAsync(Cdn, default);

		Assert.Equal("image/png", first.Value.ContentType);
		Assert.Equal(png, second.Value.Data);
		Assert.Equal(1, calls);
	}

	[Theory]
	[InlineData("https://example.com/logo.png")]
	[InlineData("http://moddbcdn.vintagestory.at/logo.png")]
	[InlineData("https://moddbcdn.vintagestory.at.evil.example/logo.png")]
	[InlineData("file:///etc/passwd")]
	[InlineData("")]
	public async Task Чужой_хост_и_не_https_отклоняются_без_запроса(string address)
	{
		var calls = 0;
		var proxy = Proxy(_ => { calls++; return Image(png, "image/png"); });

		var result = await proxy.GetAsync(address, default);

		Assert.False(result.IsSuccess);
		Assert.Equal(0, calls);
	}

	[Fact]
	public async Task Переадресация_не_выполняется()
	{
		var proxy = Proxy(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.com/x.png") } });

		Assert.False((await proxy.GetAsync(Cdn, default)).IsSuccess);
	}

	[Fact]
	public async Task Svg_отклоняется_и_по_заголовку_и_по_содержимому()
	{
		var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");

		Assert.False((await Proxy(_ => Image(svg, "image/svg+xml")).GetAsync(Cdn, default)).IsSuccess);
		Assert.False((await Proxy(_ => Image(svg, "image/png")).GetAsync(Cdn, default)).IsSuccess);
	}

	[Fact]
	public async Task Заголовок_png_с_html_внутри_вместо_картинки_отклоняется()
	{
		var html = Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");

		Assert.False((await Proxy(_ => Image(html, "image/png")).GetAsync(Cdn, default)).IsSuccess);
	}

	[Fact]
	public async Task Превышение_размера_отклоняется()
	{
		var big = new byte[ModImageProxy.MaxImageBytes + 1];
		png.CopyTo(big, 0);

		Assert.False((await Proxy(_ => Image(big, "image/png")).GetAsync(Cdn, default)).IsSuccess);
	}

	[Fact]
	public async Task Кэш_вытесняет_давние_картинки_при_переполнении()
	{
		var calls = 0;
		var body = new byte[ModImageProxy.MaxImageBytes];
		png.CopyTo(body, 0);
		var clock = new ManualClock();
		var proxy = new ModImageProxy(new HttpClient(new Handler(_ => { calls++; return Image(body, "image/png"); })), clock);

		// 16 МБ / 2 МБ = 8 картинок; девятая вытесняет первую.
		for (var index = 0; index < 34; index++)
		{
			clock.Advance(TimeSpan.FromSeconds(1));
			Assert.True((await proxy.GetAsync($"https://moddbcdn.vintagestory.at/{index}.png", default)).IsSuccess);
		}

		await proxy.GetAsync("https://moddbcdn.vintagestory.at/0.png", default);

		Assert.Equal(35, calls);
	}

	[Theory]
	[InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
	[InlineData(new byte[] { (byte) 'G', (byte) 'I', (byte) 'F', (byte) '8', (byte) '9', (byte) 'a' }, "image/gif")]
	[InlineData(new byte[] { (byte) 'R', (byte) 'I', (byte) 'F', (byte) 'F', 0, 0, 0, 0, (byte) 'W', (byte) 'E', (byte) 'B', (byte) 'P' }, "image/webp")]
	[InlineData(new byte[] { 1, 2, 3 }, null)]
	public void Тип_определяется_по_первым_байтам(byte[] data, string? expected) =>
		Assert.Equal(expected, ModImageProxy.Sniff(data));

	[Fact]
	public void Адрес_картинки_для_браузера_только_с_CDN()
	{
		Assert.Equal("/api/moddb/image?url=" + Uri.EscapeDataString(Cdn), ModDbClient.ProxiedImage(Cdn));
		Assert.Null(ModDbClient.ProxiedImage("https://example.com/a.png"));
		Assert.Null(ModDbClient.ProxiedImage(null));
	}

	// ---------- клиент ----------

	[Fact]
	public async Task Страница_мода_несёт_описание_и_скриншоты_через_прокси()
	{
		var mod = new JsonObject
		{
			["mod"] = new JsonObject
			{
				["name"] = "Example",
				["text"] = "<p>hello</p>",
				["logofile"] = Cdn,
				["screenshots"] = new JsonArray(
					new JsonObject { ["mainfile"] = "https://moddbcdn.vintagestory.at/a.png", ["thumbnailfilename"] = "https://moddbcdn.vintagestory.at/a_t.png" },
					new JsonObject { ["mainfile"] = "https://example.com/evil.png" }),
				["downloads"] = 10,
				["releases"] = new JsonArray(),
			},
		};
		var client = Client(new Counter(), extra: new() { ["/api/mod/example"] = mod });

		var json = (await client.DetailsAsync("example", game, string.Empty, default)).Value.Json;

		Assert.Equal("<p>hello</p>", json["text"]!.GetValue<string>());
		Assert.StartsWith("/api/moddb/image?url=", json["logo"]!.GetValue<string>(), StringComparison.Ordinal);
		var shots = json["screenshots"]!.AsArray();
		Assert.Single(shots);
		Assert.Contains(Uri.EscapeDataString("a_t.png"), shots[0]!["thumbnail"]!.GetValue<string>(), StringComparison.Ordinal);
		Assert.Equal(10, json["downloads"]!.GetValue<int>());
	}

	// ---------- окружение ----------

	private static ModImageProxy Proxy(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
		new(new HttpClient(new Handler(respond)), TimeProvider.System);

	private static HttpResponseMessage Image(byte[] data, string type)
	{
		var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
		response.Content.Headers.ContentType = new(type);
		return response;
	}

	private static JsonArray Listing(int count) =>
		new([.. Enumerable.Range(1, count).Select(index => (JsonNode) new JsonObject
		{
			["modid"] = index,
			["name"] = $"Мод {index}",
			["type"] = "mod",
			["side"] = "both",
			["tags"] = new JsonArray("QoL"),
			["downloads"] = index,
			["modidstrs"] = new JsonArray($"mod{index}"),
		})]);

	private static ModDbClient Client(
		Counter counter,
		JsonArray? catalogue = null,
		long[]? compatible = null,
		Func<bool>? available = null,
		Dictionary<string, JsonObject>? extra = null) =>
		new(new HttpClient(new Handler(request =>
		{
			counter.Hit(request.RequestUri!.AbsolutePath, request.RequestUri.Query);
			if (available is not null && !available())
			{
				return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
			}

			var path = request.RequestUri!.AbsolutePath;
			JsonObject? body = path switch
			{
				"/api/mods" when request.RequestUri.Query.Contains("gameversions", StringComparison.Ordinal) =>
					new JsonObject { ["mods"] = new JsonArray([.. (compatible ?? []).Select(id => (JsonNode) new JsonObject { ["modid"] = id })]) },
				"/api/mods" => new JsonObject { ["mods"] = catalogue ?? [] },
				"/api/tags" => new JsonObject { ["tags"] = new JsonArray(new JsonObject { ["name"] = "QoL", ["color"] = "#fff" }) },
				"/api/gameversions" => new JsonObject { ["gameversions"] = new JsonArray(new JsonObject { ["tagid"] = -5, ["name"] = "1.22.7" }) },
				_ => extra is not null && extra.TryGetValue(path, out var value) ? value : null,
			};
			return body is null
				? new HttpResponseMessage(HttpStatusCode.NotFound)
				: new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
		})));

	private sealed class Counter
	{
		private readonly List<(string Path, string Query)> hits = [];

		public void Hit(string path, string query)
		{
			lock (hits)
			{
				hits.Add((path, query));
			}
		}

		/// <summary>Сколько запросов к пути: с указанной частью запроса или, если она пуста, без параметра версий игры.</summary>
		public int Of(string path, string queryPart)
		{
			lock (hits)
			{
				return hits.Count(hit => hit.Path == path
					&& (queryPart.Length == 0
						? !hit.Query.Contains("gameversions", StringComparison.Ordinal)
						: hit.Query.Contains(queryPart, StringComparison.Ordinal)));
			}
		}
	}

	private sealed class ManualClock : TimeProvider
	{
		private long ticks;

		public override long GetTimestamp() => ticks;

		public override long TimestampFrequency => TimeSpan.TicksPerSecond;

		public void Advance(TimeSpan span) => ticks += span.Ticks;
	}

	private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(respond(request));
	}
}
