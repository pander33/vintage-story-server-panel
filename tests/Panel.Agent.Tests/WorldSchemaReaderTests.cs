using System.IO.Compression;
using Panel.Agent.Content;
using Panel.Configuration.Domain;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Tests;

/// <summary>
/// Чтение схемы параметров мира: разбор сборок и архивов модов, слияние источников, кэш по файлу.
/// </summary>
/// <remarks>
/// Разбор атрибута <c>ModInfoAttribute</c> в настоящей сборке (<c>Mods/VSSurvivalMod.dll</c> игры
/// 1.22.7) проверен вручную при разработке: 5 стилей игры и 64 параметра читаются корректно,
/// подписи находятся в <c>ru.json</c>. Построить такую сборку в тесте без стороннего компилятора
/// или API игры нельзя, поэтому здесь проверяются путь модов без своей сборки (<c>modinfo.json</c>),
/// путь через собственную .dll мода (когда `worldConfig` в manifest нет) на обычной сборке без
/// атрибута — она не должна ничего дать и не должна упасть, — и всё вокруг: слияние, нападения,
/// кэш.
/// </remarks>
public sealed class WorldSchemaReaderTests : IDisposable
{
	private readonly string root = Path.Combine(Path.GetTempPath(), "world-schema-tests", Guid.NewGuid().ToString("n"));

	public WorldSchemaReaderTests() => Directory.CreateDirectory(root);

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

	[Fact]
	public void Без_модов_и_без_сборок_игры_схема_пуста()
	{
		var schema = new WorldSchemaReader().Read(Server());

		Assert.True(schema.IsSuccess);
		Assert.Empty(schema.Value.Playstyles);
		Assert.Empty(schema.Value.Attributes);
		Assert.Empty(schema.Value.Warnings);
	}

	[Fact]
	public void Обычная_net_сборка_без_ModInfoAttribute_не_падает_и_ничего_не_даёт()
	{
		var server = Server();
		File.Copy(typeof(WorldSchemaReader).Assembly.Location, Path.Combine(server.ProgramDirectory, "Mods", "VSSurvivalMod.dll"));

		var schema = new WorldSchemaReader().Read(server);

		Assert.True(schema.IsSuccess);
		Assert.Empty(schema.Value.Playstyles);
		Assert.Empty(schema.Value.Attributes);
	}

	[Fact]
	public void Мод_объявляет_схему_объектом_в_modinfo_json()
	{
		var server = Server();
		WriteModArchive(server, "example.zip", modinfo: """
			{"modid": "example", "version": "1.0.0", "worldConfig": {"worldConfigAttributes": [{"code": "myAttr", "dataType": "Bool", "category": "worldgen"}]}}
			""");

		var schema = new WorldSchemaReader().Read(server);

		Assert.True(schema.IsSuccess);
		var attribute = Assert.Single(schema.Value.Attributes);
		Assert.Equal("myAttr", attribute.Code);
		Assert.Equal("mod:example.zip", attribute.Source);
	}

	[Fact]
	public void Мод_объявляет_схему_строкой_в_modinfo_json()
	{
		var server = Server();
		var payload = """{"worldConfigAttributes": [{"code": "stringy", "dataType": "String", "category": "worldgen"}]}""".Replace("\"", "\\\"");
		WriteModArchive(server, "example.zip", modinfo: $$"""{"modid": "example", "version": "1.0.0", "worldConfig": "{{payload}}"}""");

		var schema = new WorldSchemaReader().Read(server);

		Assert.True(schema.IsSuccess);
		Assert.Equal("stringy", Assert.Single(schema.Value.Attributes).Code);
	}

	[Fact]
	public void Ключ_worldConfig_ищется_без_учёта_регистра()
	{
		var server = Server();
		WriteModArchive(server, "example.zip", modinfo: """{"modid": "example", "version": "1.0.0", "WorldConfig": {"worldConfigAttributes": [{"code": "cased", "dataType": "Bool"}]}}""");

		var schema = new WorldSchemaReader().Read(server);

		Assert.Equal("cased", Assert.Single(schema.Value.Attributes).Code);
	}

	[Fact]
	public void Мод_без_worldConfig_и_без_dll_даёт_пустую_схему_без_отказа()
	{
		var server = Server();
		WriteModArchive(server, "plain.zip", modinfo: """{"modid": "plain", "version": "1.0.0"}""");

		var schema = new WorldSchemaReader().Read(server);

		Assert.True(schema.IsSuccess);
		Assert.Empty(schema.Value.Attributes);
	}

	[Fact]
	public void Мод_без_worldConfig_но_с_dll_в_корне_читает_её()
	{
		var server = Server();
		var path = Path.Combine(root, "with-dll.zip");
		using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
		{
			archive.CreateEntryFromFile(WriteText("modinfo.json", """{"modid": "hasdll", "version": "1.0.0"}"""), "modinfo.json");
			archive.CreateEntry("plain.dll").Open().Dispose();
		}

		Directory.CreateDirectory(server.ModsDirectory);
		File.Copy(path, Path.Combine(server.ModsDirectory, "with-dll.zip"));

		var schema = new WorldSchemaReader().Read(server);

		// Пустой .dll — не образ PE: источник даёт отказ, который уходит в предупреждение, а не
		// роняет чтение схемы сервера целиком.
		Assert.True(schema.IsSuccess);
		Assert.Single(schema.Value.Warnings);
		Assert.Contains("with-dll.zip", schema.Value.Warnings[0].ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public void Слишком_много_записей_в_архиве_даёт_предупреждение_а_не_падение()
	{
		var server = Server();
		var path = Path.Combine(root, "huge.zip");
		using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
		{
			for (var i = 0; i < 20_001; i++)
			{
				archive.CreateEntry($"file{i}.txt").Open().Dispose();
			}
		}

		File.Copy(path, Path.Combine(server.ModsDirectory, "huge.zip"));

		var schema = new WorldSchemaReader().Read(server);

		Assert.True(schema.IsSuccess);
		Assert.Single(schema.Value.Warnings);
	}

	[Fact]
	public void Несколько_модов_объединяются_в_порядке_имён_файлов()
	{
		var server = Server();
		WriteModArchive(server, "b.zip", modinfo: """{"modid": "b", "version": "1.0", "worldConfig": {"worldConfigAttributes": [{"code": "shared", "dataType": "Bool"}]}}""");
		WriteModArchive(server, "a.zip", modinfo: """{"modid": "a", "version": "1.0", "worldConfig": {"worldConfigAttributes": [{"code": "shared", "dataType": "String"}]}}""");

		var schema = new WorldSchemaReader().Read(server);

		// a.zip идёт раньше b.zip по алфавиту, поэтому его объявление побеждает.
		Assert.Equal(WorldAttributeType.Text.ToString(), schema.Value.Attributes.Single().Type.ToString());
		Assert.Single(schema.Value.Warnings);
	}

	[Fact]
	public void Чтение_кэшируется_по_пути_размеру_и_времени_изменения()
	{
		var server = Server();
		WriteModArchive(server, "example.zip", modinfo: """{"modid": "example", "version": "1.0", "worldConfig": {"worldConfigAttributes": [{"code": "first", "dataType": "Bool"}]}}""");
		var reader = new WorldSchemaReader();
		var first = reader.Read(server);
		Assert.Equal("first", Assert.Single(first.Value.Attributes).Code);

		// Тот же путь, то же время изменения и тот же размер (одно и то же число символов) —
		// перечитан не будет, отдаётся прежний разбор.
		var path = Path.Combine(server.ModsDirectory, "example.zip");
		var stamp = File.GetLastWriteTimeUtc(path);
		WriteModArchive(server, "example.zip", modinfo: """{"modid": "example", "version": "1.0", "worldConfig": {"worldConfigAttributes": [{"code": "secnd", "dataType": "Bool"}]}}""");
		File.SetLastWriteTimeUtc(path, stamp);

		var second = reader.Read(server);
		Assert.Equal("first", Assert.Single(second.Value.Attributes).Code);
	}

	[Fact]
	public void Изменение_файла_сбрасывает_кэш()
	{
		var server = Server();
		WriteModArchive(server, "example.zip", modinfo: """{"modid": "example", "version": "1.0", "worldConfig": {"worldConfigAttributes": [{"code": "first", "dataType": "Bool"}]}}""");
		var reader = new WorldSchemaReader();
		reader.Read(server);

		File.Delete(Path.Combine(server.ModsDirectory, "example.zip"));
		WriteModArchive(server, "example.zip", modinfo: """{"modid": "example", "version": "1.0.1", "worldConfig": {"worldConfigAttributes": [{"code": "second", "dataType": "Bool"}]}}""");
		Thread.Sleep(50);
		File.SetLastWriteTimeUtc(Path.Combine(server.ModsDirectory, "example.zip"), DateTime.UtcNow);

		var second = reader.Read(server);

		Assert.Equal("second", Assert.Single(second.Value.Attributes).Code);
	}

	[Fact]
	public void Повторяющийся_ключ_в_modinfo_не_роняет_чтение()
	{
		var server = Server();
		WriteModArchive(server, "dup.zip", modinfo: """{"modid": "dup", "modid": "dup", "version": "1.0", "worldConfig": {"worldConfigAttributes": [{"code": "x", "dataType": "bool"}]}}""");

		var schema = new WorldSchemaReader().Read(server);

		Assert.True(schema.IsSuccess);
	}

	[Fact]
	public void Одноимённые_моды_двух_серверов_кэшируются_раздельно()
	{
		var reader = new WorldSchemaReader();
		var first = Server();
		WriteModArchive(first, "same.zip", modinfo: """{"modid": "a", "version": "1.0", "worldConfig": {"worldConfigAttributes": [{"code": "fromA", "dataType": "bool"}]}}""");
		var second = first with { ModsDirectory = Path.Combine(root, "other", "Mods") };
		WriteModArchive(second, "same.zip", modinfo: """{"modid": "b", "version": "1.0", "worldConfig": {"worldConfigAttributes": [{"code": "fromB", "dataType": "bool"}]}}""");
		File.SetLastWriteTimeUtc(Path.Combine(second.ModsDirectory, "same.zip"), File.GetLastWriteTimeUtc(Path.Combine(first.ModsDirectory, "same.zip")));

		Assert.Equal("fromA", Assert.Single(reader.Read(first).Value.Attributes).Code);
		Assert.Equal("fromB", Assert.Single(reader.Read(second).Value.Attributes).Code);
	}

	// ---------- языковые файлы ----------

	[Fact]
	public void Подписи_собираются_из_игры_и_модов()
	{
		var server = Server();
		var lang = Path.Combine(server.ProgramDirectory, "assets", "game", "lang");
		Directory.CreateDirectory(lang);
		File.WriteAllText(Path.Combine(lang, "ru.json"), """{"worldattribute-game": "Игра"}""");
		File.WriteAllText(Path.Combine(lang, "en.json"), """{"worldattribute-game": "Game", "worldattribute-fallback": "Fallback"}""");
		WriteModArchiveLang(server, "mod.zip", """{"worldattribute-mod": "Мод"}""");

		var pack = new WorldSchemaReader().ReadLanguage(server, "ru", "en");

		Assert.Equal("Игра", pack.Label("worldattribute-game", ""));
		Assert.Equal("Fallback", pack.Label("worldattribute-fallback", ""));
		Assert.Equal("Мод", pack.Label("worldattribute-mod", ""));
		Assert.Equal("worldattribute-unknown", pack.Label("worldattribute-unknown", ""));
	}

	[Fact]
	public void Без_каталога_ассетов_подписи_пусты_но_не_падают()
	{
		var pack = new WorldSchemaReader().ReadLanguage(Server(), "ru", "en");

		Assert.Equal("x", pack.Label("x", ""));
	}

	// ---------- вспомогательное ----------

	private void WriteModArchive(RegisteredServer server, string fileName, string modinfo)
	{
		Directory.CreateDirectory(server.ModsDirectory);
		var path = Path.Combine(server.ModsDirectory, fileName);
		File.Delete(path);
		using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
		using var stream = archive.CreateEntry("modinfo.json").Open();
		using var writer = new StreamWriter(stream);
		writer.Write(modinfo);
	}

	private void WriteModArchiveLang(RegisteredServer server, string fileName, string ruJson)
	{
		Directory.CreateDirectory(server.ModsDirectory);
		using var archive = ZipFile.Open(Path.Combine(server.ModsDirectory, fileName), ZipArchiveMode.Create);
		using (var stream = archive.CreateEntry("modinfo.json").Open())
		using (var writer = new StreamWriter(stream))
		{
			writer.Write("""{"modid": "langmod", "version": "1.0"}""");
		}

		using (var stream = archive.CreateEntry("assets/langmod/lang/ru.json").Open())
		using (var writer = new StreamWriter(stream))
		{
			writer.Write(ruJson);
		}
	}

	private string WriteText(string name, string content)
	{
		var path = Path.Combine(root, name);
		File.WriteAllText(path, content);
		return path;
	}
}
