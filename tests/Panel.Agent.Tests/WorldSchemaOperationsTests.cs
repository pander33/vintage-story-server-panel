using System.IO.Compression;
using System.Text.Json;
using Panel.Agent.Content;
using Panel.Agent.Transport;
using Panel.Contracts;
using Panel.Contracts.Agent;
using Panel.Contracts.Configuration;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Tests;

/// <summary>Ответ <c>world-config-schema</c>: подписи разрешены демоном, типы переведены в слова протокола.</summary>
public sealed class WorldSchemaOperationsTests : IDisposable
{
	private readonly string root = Path.Combine(Path.GetTempPath(), "world-schema-ops", Guid.NewGuid().ToString("n"));

	public WorldSchemaOperationsTests() => Directory.CreateDirectory(root);

	public void Dispose()
	{
		try
		{
			Directory.Delete(root, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	[Fact]
	public async Task Подписи_типы_и_варианты_приходят_готовыми()
	{
		var server = Server();
		var lang = Path.Combine(server.ProgramDirectory, "assets", "game", "lang");
		Directory.CreateDirectory(lang);
		File.WriteAllText(Path.Combine(lang, "ru.json"), """
			{
				"worldattribute-worldWidth": "Ширина мира",
				"worldattribute-worldWidth-desc": "Сколько блоков",
				"worldconfig-worldWidth-1 mil blocks": "1 млн блоков",
				"worldconfig-category-worldgen": "Генерация мира",
				"playstyle-preset-custom": "Свой стиль",
				"playstyle-desc-preset-custom": "Описание стиля"
			}
			""");
		WriteMod(server, "custom.zip", """
			{"modid": "custom", "version": "1.0", "worldConfig": {
				"playstyles": [{"code": "custom", "langCode": "preset-custom", "worldConfig": {"worldWidth": "1024000"}}],
				"worldConfigAttributes": [
					{"code": "worldWidth", "dataType": "DropDown", "category": "worldgen", "values": ["1024000", "512000"], "names": ["1 mil blocks", "512k blocks"], "default": "1024000", "onlyDuringWorldCreate": true},
					{"code": "temporalStorms", "dataType": "Bool", "category": "temporalstability", "default": "true"},
					{"code": "futureThing", "dataType": "Slider"}
				]
			}}
			""");

		var reply = await new WorldSchemaOperations(new WorldSchemaReader())
			.ExecuteAsync(new AgentRequest { Op = AgentProtocol.OpWorldConfigSchema, Instance = "example", Args = ["ru"] }, server, CancellationToken.None);

		Assert.True(reply.Ok);
		var payload = reply.Result.Deserialize<WorldSchemaPayload>(PanelJson.Options)!;

		var style = Assert.Single(payload.Playstyles);
		Assert.Equal("Свой стиль", style.Label);
		Assert.Equal("Описание стиля", style.Description);
		Assert.Equal("1024000", style.Values["worldWidth"]);

		var width = payload.Attributes.Single(attribute => attribute.Code == "worldWidth");
		Assert.Equal("Ширина мира", width.Label);
		Assert.Equal("Сколько блоков", width.Description);
		Assert.Equal("dropdown", width.DataType);
		Assert.True(width.OnlyDuringWorldCreate);
		Assert.Equal("1 млн блоков", width.Options[0].Label);
		Assert.Equal("1024000", width.Options[0].Value);

		// Перевода нет — подпись из names, а не код значения.
		Assert.Equal("512k blocks", width.Options[1].Label);

		Assert.Equal("bool", payload.Attributes.Single(attribute => attribute.Code == "temporalStorms").DataType);
		Assert.Empty(payload.Attributes.Single(attribute => attribute.Code == "temporalStorms").Options);

		// Неизвестный тип не отбрасывается, форма покажет его текстовым полем.
		Assert.Equal("unknown", payload.Attributes.Single(attribute => attribute.Code == "futureThing").DataType);

		Assert.Equal("Генерация мира", payload.Categories.Single(category => category.Id == "worldgen").Label);

		// Перевода раздела нет — подпись сам код.
		Assert.Equal("temporalstability", payload.Categories.Single(category => category.Id == "temporalstability").Label);
	}

	[Theory]
	[InlineData("en")]
	[InlineData(null)]
	[InlineData("../ru")]
	public async Task Английский_интерфейс_получает_английские_подписи_без_русского_запасного(string? code)
	{
		var server = Server();
		var lang = Path.Combine(server.ProgramDirectory, "assets", "game", "lang");
		Directory.CreateDirectory(lang);
		File.WriteAllText(Path.Combine(lang, "ru.json"), """
			{
				"worldattribute-worldWidth": "Ширина мира",
				"worldattribute-worldWidth-desc": "Сколько блоков",
				"worldconfig-category-worldgen": "Генерация мира"
			}
			""");
		File.WriteAllText(Path.Combine(lang, "en.json"), """
			{
				"worldattribute-worldWidth": "World width",
				"worldconfig-category-worldgen": "World generation"
			}
			""");
		WriteMod(server, "custom.zip", """
			{"modid": "custom", "version": "1.0", "worldConfig": {
				"worldConfigAttributes": [{"code": "worldWidth", "dataType": "Integer", "category": "worldgen"}]
			}}
			""");

		var reply = await new WorldSchemaOperations(new WorldSchemaReader())
			.ExecuteAsync(new AgentRequest { Op = AgentProtocol.OpWorldConfigSchema, Instance = "example", Args = code is null ? [] : [code] }, server, CancellationToken.None);

		var payload = reply.Result.Deserialize<WorldSchemaPayload>(PanelJson.Options)!;
		var width = Assert.Single(payload.Attributes);
		Assert.Equal("World width", width.Label);

		// Английского описания нет — пусто, а не русское.
		Assert.Equal(string.Empty, width.Description);
		Assert.Equal("World generation", Assert.Single(payload.Categories).Label);
	}

	[Fact]
	public async Task Диспетчер_направляет_операцию_в_группу_схемы()
	{
		var reply = await new WorldSchemaOperations(new WorldSchemaReader())
			.ExecuteAsync(new AgentRequest { Op = AgentProtocol.OpWorldConfigSchema, Instance = "example", Args = [] }, Server(), CancellationToken.None);

		Assert.True(reply.Ok);
		Assert.True(WorldSchemaOperations.Handles(AgentProtocol.OpWorldConfigSchema));
		Assert.False(WorldSchemaOperations.Handles(AgentProtocol.OpConfigGet));
		var payload = reply.Result.Deserialize<WorldSchemaPayload>(PanelJson.Options)!;
		Assert.Empty(payload.Attributes);
		Assert.Empty(payload.Warnings);
	}

	private RegisteredServer Server()
	{
		var program = Path.Combine(root, "server");
		var mods = Path.Combine(root, "data", "Mods");
		Directory.CreateDirectory(Path.Combine(program, "Mods"));
		Directory.CreateDirectory(mods);
		return new RegisteredServer
		{
			Id = InstanceId.Parse("example"),
			Name = "Example",
			Unit = "vs-example",
			GameVersion = "1.22.7",
			ProgramDirectory = program,
			ModsDirectory = mods,
			DataDirectory = Path.Combine(root, "data"),
			WorldDirectory = Path.Combine(root, "data", "Saves"),
			ConfigFile = Path.Combine(root, "data", "serverconfig.json"),
		};
	}

	private static void WriteMod(RegisteredServer server, string name, string modinfo)
	{
		using var archive = ZipFile.Open(Path.Combine(server.ModsDirectory, name), ZipArchiveMode.Create);
		using var writer = new StreamWriter(archive.CreateEntry("modinfo.json").Open());
		writer.Write(modinfo);
	}
}
