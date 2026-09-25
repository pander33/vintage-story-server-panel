using System.Text.Json.Nodes;
using Panel.Catalogues.Domain;
using Panel.Catalogues.Infrastructure;
using Panel.SharedKernel;

namespace Panel.Catalogues.Tests;

/// <summary>Каталог версий игры.</summary>
public sealed class CatalogueTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "vs-catalogue-" + Guid.NewGuid().ToString("n"));

	private string CacheFile => Path.Combine(directory, "catalogue.json");

	public void Dispose()
	{
		if (Directory.Exists(directory))
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	private static JsonObject Entry(string version, string key = "linuxserver", string md5 = "00000000000000000000000000000000",
		string host = "cdn.vintagestory.at") => new()
		{
			[key] = new JsonObject
			{
				["filename"] = $"vs_server_linux-x64_{version}.tar.gz",
				["filesize"] = "123.4 MB",
				["md5"] = md5,
				["urls"] = new JsonObject { ["cdn"] = $"https://{host}/gamefiles/stable/vs_server_linux-x64_{version}.tar.gz" },
				["latest"] = 1,
			},
		};

	private static JsonObject Manifest() => new()
	{
		["1.22.7"] = Entry("1.22.7"),
		["1.22.0-rc.1"] = Entry("1.22.0-rc.1"),
		["1.21.5"] = Entry("1.21.5"),
		["1.20.0"] = Entry("1.20.0", "windowsserver"),
	};

	[Fact]
	public void Размер_читается_из_человеческой_записи()
	{
		Assert.Equal((long) (590.5 * 1024 * 1024), ReleaseManifest.ParseSize("590.5 MB"));
		Assert.Equal(2L * 1024 * 1024 * 1024, ReleaseManifest.ParseSize("2 GB"));
		Assert.Equal(1536, ReleaseManifest.ParseSize("1,5 KiB"));
		Assert.Equal(0, ReleaseManifest.ParseSize(""));
		Assert.Equal(0, ReleaseManifest.ParseSize("огромный"));
		Assert.Equal(0, ReleaseManifest.ParseSize("1.2.3 MB"));
	}

	[Fact]
	public void Разбор_оставляет_серверные_пакеты_платформы_от_новых_к_старым()
	{
		var linux = ReleaseManifest.Parse(Manifest(), ServerPlatform.Linux);

		Assert.Equal(["1.22.7", "1.22.0-rc.1", "1.21.5"], linux.Select(item => item.Version.Text));
		Assert.Equal("stable", linux[0].Channel);
		Assert.Equal("unstable", linux[1].Channel);
		Assert.Equal(["1.20.0"], ReleaseManifest.Parse(Manifest(), ServerPlatform.Windows).Select(item => item.Version.Text));
	}

	[Fact]
	public void Запись_которой_нельзя_доверять_пропускается()
	{
		var hostile = new JsonObject
		{
			["1.22.7"] = Entry("1.22.7", host: "evil.example.com"),
			["not-a-version"] = Entry("1.0.0"),
			["1.21.0"] = new JsonObject { ["linuxserver"] = "not an object" },
			["1.20.0"] = Entry("1.20.0"),
		};

		Assert.Equal(["1.20.0"], ReleaseManifest.Parse(hostile, ServerPlatform.Linux).Select(item => item.Version.Text));
	}

	[Fact]
	public void Пакет_без_годной_суммы_показывается_но_помечен()
	{
		var parsed = ReleaseManifest.Parse(new JsonObject { ["1.22.7"] = Entry("1.22.7", md5: "short") }, ServerPlatform.Linux);

		Assert.Null(parsed[0].Md5);
		Assert.Equal(string.Empty, parsed[0].ToJson()["md5"]!.GetValue<string>());
	}

	[Fact]
	public void Элемент_ответа_в_замороженной_форме()
	{
		var json = ReleaseManifest.Parse(new JsonObject { ["1.22.7"] = Entry("1.22.7") }, ServerPlatform.Linux)[0].ToJson();

		Assert.Equal(
			["version", "channel", "filename", "size_text", "size", "md5", "url", "latest"],
			json.Select(pair => pair.Key));
		Assert.True(json["latest"]!.GetValue<bool>());
	}

	[Fact]
	public async Task Снимок_кэшируется_и_не_перезапрашивается_в_течение_суток()
	{
		var calls = 0;
		var catalogue = Catalogue(_ =>
		{
			calls++;
			return Task.FromResult<Result<JsonObject>>(Manifest());
		});

		var first = (await catalogue.SnapshotAsync(false, default)).Value.ToJson();
		Assert.Equal("network", first["source"]!.GetValue<string>());
		Assert.Equal("1.22.7", first["latest_stable"]!.GetValue<string>());
		Assert.Equal("cache", (await catalogue.SnapshotAsync(false, default)).Value.ToJson()["source"]!.GetValue<string>());
		Assert.Equal(1, calls);
		Assert.True((await catalogue.SnapshotAsync(true, default)).Value.FromNetwork);
		Assert.Equal(2, calls);
	}

	[Fact]
	public async Task Устаревший_кэш_лучше_отсутствия_каталога()
	{
		Directory.CreateDirectory(directory);
		await File.WriteAllTextAsync(CacheFile, new JsonObject { ["fetched_at"] = 0, ["raw"] = Manifest() }.ToJsonString());

		var snapshot = (await Catalogue(Broken).SnapshotAsync(false, default)).Value.ToJson();

		Assert.Equal("cache", snapshot["source"]!.GetValue<string>());
		Assert.True(snapshot["stale"]!.GetValue<bool>());
		Assert.Equal("1.22.7", snapshot["latest_stable"]!.GetValue<string>());
	}

	[Fact]
	public async Task Без_кэша_отказ_сети_сообщается()
	{
		var snapshot = await Catalogue(Broken).SnapshotAsync(false, default);

		Assert.Equal("сеть недоступна", snapshot.Failure!.Message);
	}

	[Fact]
	public async Task Неизвестная_версия_называется_в_отказе()
	{
		var catalogue = Catalogue(_ => Task.FromResult<Result<JsonObject>>(Manifest()));

		Assert.Equal("1.22.7", (await catalogue.FindAsync("1.22.7", false, default)).Value.Version.Text);
		Assert.Equal("Версия 9.9.9 отсутствует в каталоге", (await catalogue.FindAsync("9.9.9", false, default)).Failure!.Message);
	}

	private static Task<Result<JsonObject>> Broken(CancellationToken ct) =>
		Task.FromResult<Result<JsonObject>>(Failure.Unavailable("сеть недоступна"));

	private VersionCatalogue Catalogue(Func<CancellationToken, Task<Result<JsonObject>>> fetch) =>
		new(CacheFile, fetch, new FixedTime(new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero)));

	private sealed class FixedTime(DateTimeOffset now) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => now;
	}
}
