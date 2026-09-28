using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Panel.Content.Domain;
using Panel.Content.Ports;
using Panel.Contracts.Content;
using Panel.SharedKernel;
using Panel.Web.Application;

namespace Panel.Web.Tests;

/// <summary>Сборка модов: выгрузка, чтение чужого ZIP и применение настроек и включения.</summary>
public sealed class ModPackExchangeTests : IDisposable
{
	private static readonly InstanceId main = InstanceId.Parse("main");
	private static readonly DateTimeOffset now = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

	private readonly string state = Path.Combine(Path.GetTempPath(), "vs-modpack-" + Guid.NewGuid().ToString("n"));
	private readonly FakeMods mods;

	public ModPackExchangeTests() => mods = new FakeMods(Path.Combine(state, "staging"));

	public void Dispose()
	{
		if (Directory.Exists(state))
		{
			Directory.Delete(state, recursive: true);
		}
	}

	[Fact]
	public async Task Выгруженная_сборка_читается_обратно()
	{
		mods.Mods.Add(Mod("carryon.zip", "carryon", "1.8.0", enabled: true));
		mods.Mods.Add(Mod("old.zip", "carryon", "1.7.0", enabled: false));
		mods.Mods.Add(Mod("off.zip", "expanded", "2.0.0", enabled: false));
		mods.Mods.Add(new ModEntryPayload { Name = "broken.zip", Enabled = true, Size = 1, Modified = 1 });
		mods.Configs["carryon.json"] = """{"range":3,"name":"Кириллица"}""";
		mods.Configs["broken.json"] = null;

		var bytes = (await ModPackExchange.ExportAsync(mods, main, GameVersionText.From("1.21.1"), now, CancellationToken.None)).Value;
		var contents = ModPackExchange.Read(new MemoryStream(bytes)).Value;

		Assert.Equal(["carryon", "expanded"], contents.Manifest.Mods.Select(mod => mod.ModId));
		Assert.Equal(("1.8.0", true, "carryon.zip"), (contents.Manifest.Mods[0].Version, contents.Manifest.Mods[0].Enabled, contents.Manifest.Mods[0].File));
		Assert.False(contents.Manifest.Mods[1].Enabled);
		Assert.Equal(("main", "1.21.1", now), (contents.Manifest.Server, contents.Manifest.GameVersion, contents.Manifest.ExportedAt));
		Assert.Equal(["carryon.json"], contents.Manifest.Configs);
		Assert.Equal("broken.json", Assert.Single(contents.Manifest.SkippedConfigs).Name);
		Assert.Equal("Кириллица", contents.Configs[ModConfigName.Parse("carryon.json")]["name"]!.GetValue<string>());
		Assert.Contains("Кириллица", Encoding.UTF8.GetString(contents.Files[ModConfigName.Parse("carryon.json")]), StringComparison.Ordinal);
	}

	[Fact]
	public void Не_zip_отвергается() =>
		Assert.Contains("не является ZIP", ModPackExchange.Read(new MemoryStream("not a zip"u8.ToArray())).Failure!.Message, StringComparison.Ordinal);

	[Theory]
	[InlineData("mods/evil.zip", "Лишний файл в сборке")]
	[InlineData("../escape.json", "за пределы каталога")]
	[InlineData("config/unlisted.json", "нет в описи сборки")]
	public void Посторонняя_запись_отвергается(string name, string message)
	{
		var zip = Zip(("modpack.json", Manifest("[]")), (name, "{}"));

		Assert.Contains(message, ModPackExchange.Read(zip).Failure!.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Настройка_из_описи_обязана_быть_в_архиве() =>
		Assert.Contains("из описи нет в сборке", ModPackExchange.Read(Zip(("modpack.json", Manifest("""["a.json"]""")))).Failure!.Message, StringComparison.Ordinal);

	[Fact]
	public void Без_описи_отвергается() =>
		Assert.Contains("нет modpack.json", ModPackExchange.Read(Zip(("config/a.json", "{}"))).Failure!.Message, StringComparison.Ordinal);

	[Theory]
	[InlineData("42")]
	[InlineData("{broken")]
	[InlineData("{\"a\":1,/*comment*/\"b\":2}")]
	public void Настройка_не_объект_json_отвергается(string json) =>
		Assert.Contains("не является объектом или массивом JSON",
			ModPackExchange.Read(Zip(("modpack.json", Manifest("""["a.json"]""")), ("config/a.json", json))).Failure!.Message,
			StringComparison.Ordinal);

	[Fact]
	public void Слишком_большая_настройка_отвергается_до_чтения()
	{
		var big = "[" + string.Join(',', Enumerable.Repeat("1", (int) (ModPack.MaximumConfigBytes / 2) + 1)) + "]";

		Assert.Contains("больше 2 МБ",
			ModPackExchange.Read(Zip(("modpack.json", Manifest("""["a.json"]""")), ("config/a.json", big))).Failure!.Message,
			StringComparison.Ordinal);
	}

	[Fact]
	public void Совпадающие_без_учёта_регистра_имена_отвергаются() =>
		Assert.Contains("без учёта регистра",
			ModPackExchange.Read(Zip(("modpack.json", Manifest("""["a.json","A.json"]""")), ("config/a.json", "{}"), ("config/A.json", "{}"))).Failure!.Message,
			StringComparison.Ordinal);

	[Fact]
	public async Task Применение_пишет_выбранные_настройки_и_переключает_моды()
	{
		mods.Mods.Add(Mod("a.zip", "a", "1.0.0", enabled: false));
		mods.Mods.Add(Mod("extra.zip", "extra", "1.0.0", enabled: true));
		mods.Configs["a.json"] = """{"x":1}""";
		var contents = ModPackExchange.Read(Zip(
			("modpack.json", Manifest("""["a.json","b.json"]""", """[{"modid":"a","version":"1.0.0","enabled":true}]""")),
			("config/a.json", """{"x":2}"""),
			("config/b.json", """{"y":1}"""))).Value;

		var plan = (await ModPackExchange.PlanAsync(mods, main, contents, GameVersionText.From(null), CancellationToken.None)).Value;
		Assert.Equal([("a.json", "changed"), ("b.json", "new")], plan.Configs.Select(config => (config.Name, config.Status)));

		var staging = new StagingWriter(new PanelOptions { StateDirectory = state });
		var applied = await ModPackExchange.ApplyAsync(
			mods, staging, main, contents,
			[ModConfigName.Parse("b.json")],
			ModPack.Toggles(plan, matchSwitches: true, disableExtra: true),
			CancellationToken.None);

		Assert.True(applied.Ok);
		Assert.True(applied.RestartRequired);
		Assert.Equal(["b.json"], applied.Configs);
		Assert.Equal(["a.zip", "extra.zip"], applied.Toggled);
		Assert.Equal("""{"y":1}""", mods.Written["b.json"]);
		Assert.False(mods.Written.ContainsKey("a.json"));
		Assert.Empty(Directory.EnumerateFiles(Path.Combine(state, "staging")));
	}

	[Fact]
	public async Task Отказ_шага_не_отменяет_остальные()
	{
		mods.Mods.Add(Mod("a.zip", "a", "1.0.0", enabled: false));
		mods.RefuseWrite = "b.json";
		var contents = ModPackExchange.Read(Zip(
			("modpack.json", Manifest("""["a.json","b.json"]""", """[{"modid":"a","version":"1.0.0","enabled":true}]""")),
			("config/a.json", "{}"),
			("config/b.json", "{}"))).Value;
		var plan = (await ModPackExchange.PlanAsync(mods, main, contents, GameVersionText.From(null), CancellationToken.None)).Value;

		var applied = await ModPackExchange.ApplyAsync(
			mods, new StagingWriter(new PanelOptions { StateDirectory = state }), main, contents,
			[ModConfigName.Parse("a.json"), ModConfigName.Parse("b.json")],
			ModPack.Toggles(plan, matchSwitches: true, disableExtra: false),
			CancellationToken.None);

		Assert.False(applied.Ok);
		Assert.Equal(["a.json"], applied.Configs);
		Assert.Equal(["a.zip"], applied.Toggled);
		Assert.Equal("b.json", Assert.Single(applied.Failures).Name);
	}

	private static string Manifest(string configs, string mods = "[]") =>
		$$"""{"format":"vs-panel-modpack","version":1,"exported_at":"2026-09-28T10:00:00Z","server":"other","mods":{{mods}},"configs":{{configs}}}""";

	private static MemoryStream Zip(params (string Name, string Text)[] entries)
	{
		var buffer = new MemoryStream();
		using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
		{
			foreach (var (name, text) in entries)
			{
				using var stream = zip.CreateEntry(name).Open();
				stream.Write(Encoding.UTF8.GetBytes(text));
			}
		}

		buffer.Position = 0;
		return buffer;
	}

	private static ModEntryPayload Mod(string file, string id, string version, bool enabled) => new()
	{
		Name = file,
		Enabled = enabled,
		Size = 1,
		Modified = 1,
		ModId = id,
		Version = version,
	};

	/// <summary>Моды и настройки одного сервера в памяти; запись настроек читает файл из staging, как демон.</summary>
	private sealed class FakeMods(string staging) : IModStore
	{
		public List<ModEntryPayload> Mods { get; } = [];

		/// <summary>Имя → текст; <c>null</c> — файл есть, но не читается.</summary>
		public Dictionary<string, string?> Configs { get; } = new(StringComparer.Ordinal);

		public Dictionary<string, string> Written { get; } = new(StringComparer.Ordinal);

		public string? RefuseWrite { get; set; }

		public Task<Result<ModsPayload>> ListAsync(InstanceId instance, CancellationToken cancellationToken) =>
			Task.FromResult(Result.Ok(new ModsPayload { Mods = [.. Mods] }));

		public Task<Result<ModChangePayload>> ToggleAsync(InstanceId instance, ModArchiveName mod, CancellationToken cancellationToken)
		{
			var index = Mods.FindIndex(item => item.Name == mod.Value);
			Mods[index] = Mods[index] with { Enabled = !Mods[index].Enabled };
			return Task.FromResult(Result.Ok(new ModChangePayload { Name = mod.Value, Enabled = Mods[index].Enabled }));
		}

		public Task<Result<ModConfigsPayload>> ListConfigsAsync(InstanceId instance, CancellationToken cancellationToken) =>
			Task.FromResult(Result.Ok(new ModConfigsPayload
			{
				Files = [.. Configs.Select(config => new ModConfigEntryPayload { Name = config.Key, Size = config.Value?.Length ?? 1 })],
			}));

		public Task<Result<ModConfigPayload>> ReadConfigAsync(InstanceId instance, ModConfigName name, CancellationToken cancellationToken) =>
			Task.FromResult(Configs.TryGetValue(name.Value, out var text) && text is not null
				? Result.Ok(new ModConfigPayload { Name = name.Value, Config = JsonNode.Parse(text) })
				: Result.Fail<ModConfigPayload>(Failure.Invalid("Файл настроек повреждён")));

		public Task<Result<Done>> WriteConfigAsync(InstanceId instance, ModConfigName name, StagedFile staged, CancellationToken cancellationToken)
		{
			if (name.Value == RefuseWrite)
			{
				return Task.FromResult(Result.Fail<Done>(Failure.Conflict("Цель не является обычным файлом")));
			}

			Written[name.Value] = File.ReadAllText(Path.Combine(staging, staged.Name));
			return Task.FromResult(Done.Ok);
		}

		public Task<Result<ModChangePayload>> InstallAsync(InstanceId instance, StagedFile staged, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<Result<ModChangePayload>> DeleteAsync(InstanceId instance, ModArchiveName mod, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<Result<ModChangePayload>> RollbackAsync(InstanceId instance, ModId mod, ModArchiveName archived, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<Result<ModProfilePayload>> SaveProfileAsync(InstanceId instance, ModProfileName profile, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<Result<ModProfilePayload>> ApplyProfileAsync(InstanceId instance, ModProfileName profile, CancellationToken cancellationToken) => throw new NotSupportedException();

		public Task<Result<Done>> DeleteProfileAsync(InstanceId instance, ModProfileName profile, CancellationToken cancellationToken) => throw new NotSupportedException();
	}
}
