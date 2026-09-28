using System.Text.Json.Nodes;
using Panel.Content.Domain;
using Panel.Contracts;
using Panel.Contracts.Content;

namespace Panel.Content.Tests;

/// <summary>Сборка модов: суждение о записях, опись и план применения.</summary>
public sealed class ModPackTests
{
	[Theory]
	[InlineData("modpack.json", ModPackEntryKind.Manifest)]
	[InlineData("config/game.json", ModPackEntryKind.Config)]
	public void Опись_и_настройки_принимаются(string name, ModPackEntryKind kind) =>
		Assert.Equal(kind, ModPack.JudgeEntry(ArchiveEntry.CreateFile(name)).Value.Kind);

	[Fact]
	public void Каталог_настроек_принимается() =>
		Assert.Equal(ModPackEntryKind.Directory, ModPack.JudgeEntry(ArchiveEntry.CreateDirectory("config/")).Value.Kind);

	[Theory]
	[InlineData("mods/evil.zip")]
	[InlineData("config/nested/a.json")]
	[InlineData("config/a.txt")]
	[InlineData("config/.hidden.json")]
	[InlineData("../config/a.json")]
	[InlineData("config/../../etc/passwd")]
	[InlineData("/modpack.json")]
	[InlineData("C:/modpack.json")]
	public void Всё_прочее_отвергается(string name) =>
		Assert.False(ModPack.JudgeEntry(ArchiveEntry.CreateFile(name)).IsSuccess);

	[Fact]
	public void Ссылки_и_чужие_каталоги_отвергаются()
	{
		Assert.False(ModPack.JudgeEntry(ArchiveEntry.CreateSymbolicLink("config/a.json", "b.json")).IsSuccess);
		Assert.False(ModPack.JudgeEntry(ArchiveEntry.CreateDirectory("mods/")).IsSuccess);
	}

	[Fact]
	public void Размер_считается_до_чтения()
	{
		Assert.True(ModPack.CountBytes(0, ModPack.MaximumConfigBytes, ModPackEntryKind.Config).IsSuccess);
		Assert.False(ModPack.CountBytes(0, ModPack.MaximumConfigBytes + 1, ModPackEntryKind.Config).IsSuccess);
		Assert.False(ModPack.CountBytes(0, ModPack.MaximumManifestBytes + 1, ModPackEntryKind.Manifest).IsSuccess);
		Assert.False(ModPack.CountBytes(ModPack.MaximumUnpackedBytes - 10, 11, ModPackEntryKind.Config).IsSuccess);
		Assert.False(ModPack.CountBytes(0, -1, ModPackEntryKind.Config).IsSuccess);
	}

	[Fact]
	public void Опись_читается_и_modid_приводится_к_нижнему_регистру()
	{
		var manifest = ModPack.FromJson(Manifest("""[{"modid":"CarryOn","version":" 1.8.0 ","enabled":false}]""", """["carryon.json"]""")).Value;

		var mod = Assert.Single(manifest.Mods);
		Assert.Equal("carryon", mod.ModId);
		Assert.Equal("1.8.0", mod.Version);
		Assert.False(mod.Enabled);
		Assert.Equal(["carryon.json"], manifest.Configs);
		Assert.Equal("main", manifest.Server);
	}

	[Theory]
	[InlineData("""{"format":"other","version":1}""", "Это не сборка модов панели")]
	[InlineData("""[1]""", "Это не сборка модов панели")]
	[InlineData("""{"format":"vs-panel-modpack"}""", "нет версии формата")]
	[InlineData("""{"format":"vs-panel-modpack","version":2,"exported_at":"2026-09-28T00:00:00Z"}""", "более новой панелью (формат 2)")]
	[InlineData("""{"format":"vs-panel-modpack","version":1,"exported_at":"2026-09-28T00:00:00Z","mods":[{"modid":"../x"}]}""", "Недопустимый modid")]
	[InlineData("""{"format":"vs-panel-modpack","version":1,"exported_at":"2026-09-28T00:00:00Z","mods":[{"modid":"a"},{"modid":"A"}]}""", "указан в сборке дважды")]
	[InlineData("""{"format":"vs-panel-modpack","version":1,"exported_at":"2026-09-28T00:00:00Z","mods":[{"modid":"a","version":"1\n2"}]}""", "Недопустимая версия")]
	[InlineData("""{"format":"vs-panel-modpack","version":1,"exported_at":"2026-09-28T00:00:00Z","configs":["../x.json"]}""", "Недопустимое имя файла настроек")]
	[InlineData("""{"format":"vs-panel-modpack","version":1,"exported_at":"2026-09-28T00:00:00Z","mods":"x"}""", "повреждена")]
	[InlineData("""{"format":"vs-panel-modpack","version":1,"exported_at":"2026-09-28T00:00:00Z","mods":null}""", "повреждена")]
	[InlineData("""{"format":"vs-panel-modpack","version":1,"exported_at":"2026-09-28T00:00:00Z","configs":null}""", "повреждена")]
	[InlineData("""{"format":"vs-panel-modpack","version":1,"exported_at":"2026-09-28T00:00:00Z","skipped_configs":[{"name":"a.json","reason":42}]}""", "повреждена")]
	public void Неверная_опись_отвергается(string json, string message) =>
		Assert.Contains(message, ModPack.FromJson(JsonNode.Parse(json)).Failure!.Message, StringComparison.Ordinal);

	[Theory]
	[InlineData("null")]
	[InlineData("[]")]
	[InlineData("[null]")]
	public void Пустой_или_испорченный_список_пропущенных_не_мешает(string skipped) =>
		Assert.Empty(ModPack.FromJson(JsonNode.Parse(
			$$"""{"format":"vs-panel-modpack","version":1,"exported_at":"2026-09-28T00:00:00Z","skipped_configs":{{skipped}}}""")).Value.SkippedConfigs);

	/// <summary>
	/// Причина пропуска приходит от отправителя сборки. Фраза панели переводится, а шаблон, который
	/// не собирается, показывается как есть: иначе <c>string.Format</c> уронил бы ответ с планом.
	/// </summary>
	[Fact]
	public void Причина_пропуска_из_чужой_сборки_не_роняет_показ()
	{
		var manifest = ModPack.FromJson(JsonNode.Parse(
			"""
			{"format":"vs-panel-modpack","version":1,"exported_at":"2026-09-28T00:00:00Z","skipped_configs":[
				{"name":"a.json","reason":{"$phrase":"{3}","args":["x"]}},
				{"name":"b.json","reason":{"$phrase":"Файл больше 2 МБ"}},
				{"name":"c.json","reason":"{ одинокая скобка"},
				{"name":"d.json","reason":{"$phrase":"{0} {","args":["x"]}},
				{"name":null,"reason":null}
			]}
			""")).Value;

		var shown = manifest.SkippedConfigs
			.Select(item => (item.Name, Russian: item.Reason.ToString(), English: PhraseCatalog.Translate(item.Reason, PanelLanguage.English)))
			.ToList();

		Assert.Equal(
			[
				("a.json", "{3}", "{3}"),
				("b.json", "Файл больше 2 МБ", "The file is larger than 2 MB"),
				("c.json", "{ одинокая скобка", "{ одинокая скобка"),
				("d.json", "{0} {", "{0} {"),
				("", "", ""),
			],
			shown);
	}

	[Fact]
	public void План_сравнивает_моды_с_установленными()
	{
		var manifest = ModPack.FromJson(Manifest(
			"""
			[
				{"modid":"alpha","version":"1.0.0","enabled":true},
				{"modid":"beta","version":"2.0.0","enabled":true},
				{"modid":"gamma","version":"v3.0.0","enabled":false},
				{"modid":"delta","version":"1.0.0","enabled":true},
				{"modid":"dup","version":"1.0.0","enabled":true}
			]
			""", "[]")).Value;
		var installed = new ModsPayload
		{
			Mods =
			[
				Installed("beta.zip", "beta", "1.0.0", enabled: true),
				Installed("gamma.zip", "Gamma", "3.0.0", enabled: true),
				Installed("delta.zip", "delta", "1.0.0", enabled: true),
				Installed("dup1.zip", "dup", "1.0.0", enabled: true),
				Installed("dup2.zip", "dup", "0.9.0", enabled: false),
				Installed("extra.zip", "extra", "1.0.0", enabled: true),
				new ModEntryPayload { Name = "broken.zip", Enabled = false, Size = 1, Modified = 1 },
			],
		};

		var plan = ModPack.Plan(manifest, new Dictionary<ModConfigName, JsonNode>(), installed, new Dictionary<ModConfigName, JsonNode?>(), GameVersionText.From("1.21.1"));
		var mods = plan.Mods.ToDictionary(mod => mod.ModId);

		Assert.Equal("missing", mods["alpha"].Status);
		Assert.Null(mods["alpha"].Installed);
		Assert.Equal(("different", "1.0.0", false), (mods["beta"].Status, mods["beta"].Installed, mods["beta"].Toggle));
		Assert.Equal(("same", true, "gamma.zip"), (mods["gamma"].Status, mods["gamma"].Toggle, mods["gamma"].Archive));
		Assert.Equal("same", mods["delta"].Status);
		Assert.Equal(("ambiguous", false, (string?) null), (mods["dup"].Status, mods["dup"].Toggle, mods["dup"].Archive));
		Assert.Equal(["broken.zip", "extra.zip"], plan.Extra.Select(mod => mod.Name));
		Assert.Equal("1.21.1", plan.CurrentGameVersion);
	}

	[Fact]
	public void План_сравнивает_настройки_по_содержимому()
	{
		var manifest = ModPack.FromJson(Manifest("[]", """["a.json","b.json","c.json","d.json"]""")).Value;
		var pack = new Dictionary<ModConfigName, JsonNode>
		{
			[ModConfigName.Parse("a.json")] = JsonNode.Parse("""{"x":1}""")!,
			[ModConfigName.Parse("b.json")] = JsonNode.Parse("""{"x":1,"y":[1,2]}""")!,
			[ModConfigName.Parse("c.json")] = JsonNode.Parse("""{"x":1}""")!,
			[ModConfigName.Parse("d.json")] = JsonNode.Parse("""{"x":1}""")!,
		};
		var current = new Dictionary<ModConfigName, JsonNode?>
		{
			[ModConfigName.Parse("b.json")] = JsonNode.Parse("""{"y":[1,2],"x":1}"""),
			[ModConfigName.Parse("c.json")] = JsonNode.Parse("""{"x":2}"""),
			[ModConfigName.Parse("d.json")] = null,
		};

		var plan = ModPack.Plan(manifest, pack, new ModsPayload(), current, GameVersionText.From(null));

		Assert.Equal(
			[("a.json", "new"), ("b.json", "same"), ("c.json", "changed"), ("d.json", "changed")],
			plan.Configs.Select(config => (config.Name, config.Status)));
		Assert.Null(plan.CurrentGameVersion);
	}

	[Fact]
	public void Переключаются_только_расходящиеся_и_по_выбору_лишние()
	{
		var plan = new ModPackPlanPayload
		{
			Mods =
			[
				new ModPackModPlanPayload { ModId = "a", Status = "same", Archive = "a.zip", Toggle = true },
				new ModPackModPlanPayload { ModId = "b", Status = "same", Archive = "b.zip", Toggle = false },
				new ModPackModPlanPayload { ModId = "c", Status = "ambiguous", Toggle = true },
			],
			Extra =
			[
				new ModPackExtraPayload { Name = "x.zip", Enabled = true },
				new ModPackExtraPayload { Name = "y.zip", Enabled = false },
			],
		};

		Assert.Equal(["a.zip"], ModPack.Toggles(plan, matchSwitches: true, disableExtra: false).Select(name => name.Value));
		Assert.Equal(["a.zip", "x.zip"], ModPack.Toggles(plan, matchSwitches: true, disableExtra: true).Select(name => name.Value));
		Assert.Equal(["x.zip"], ModPack.Toggles(plan, matchSwitches: false, disableExtra: true).Select(name => name.Value));
		Assert.Empty(ModPack.Toggles(plan, matchSwitches: false, disableExtra: false));
	}

	private static JsonNode Manifest(string mods, string configs) => JsonNode.Parse(
		$$"""{"format":"vs-panel-modpack","version":1,"exported_at":"2026-09-28T10:00:00Z","server":"main","mods":{{mods}},"configs":{{configs}}}""")!;

	private static ModEntryPayload Installed(string file, string id, string version, bool enabled) => new()
	{
		Name = file,
		Enabled = enabled,
		Size = 1,
		Modified = 1,
		ModId = id,
		Version = version,
	};
}
