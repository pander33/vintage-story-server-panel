using System.Text.Json.Nodes;
using Panel.Provisioning.Domain;

namespace Panel.Provisioning.Tests;

public sealed class ServerConfigPathsTests
{
	[Theory]
	[InlineData("/srv/vintagestory/data/Saves/default.vcdbs", "C:\\ProgramData\\vs-panel\\servers\\main\\data\\Saves\\default.vcdbs")]
	[InlineData("C:\\ProgramData\\vs-panel\\data\\Saves\\world.vcdbs", "/srv/vs-panel/main/data/Saves/world.vcdbs")]
	public void Пути_мира_переносятся_между_платформами(string source, string expected)
	{
		var windows = expected.StartsWith('C');
		var target = windows
			? ServerConfigPaths.Create("C:\\ProgramData\\vs-panel\\servers\\main\\data\\Saves", "C:\\ProgramData\\vs-panel\\servers\\main\\data\\Mods")
			: ServerConfigPaths.Create("/srv/vs-panel/main/data/Saves", "/srv/vs-panel/main/data/Mods");
		var config = new JsonObject
		{
			["WorldConfig"] = new JsonObject { ["SaveFileLocation"] = source },
			["ModPaths"] = new JsonArray("Mods", windows ? "/srv/vintagestory/data/Mods" : "C:\\old\\data\\Mods"),
			["ServerDescription"] = "сохранить без изменений",
		};

		var rebased = target.Rebase(config).Value;

		Assert.Equal(expected, (string?) rebased["WorldConfig"]!["SaveFileLocation"]);
		Assert.Equal("Mods", (string?) rebased["ModPaths"]![0]);
		Assert.Equal(target.ModsDirectory, (string?) rebased["ModPaths"]![1]);
		Assert.Equal("сохранить без изменений", (string?) rebased["ServerDescription"]);
		Assert.Equal(source, (string?) config["WorldConfig"]!["SaveFileLocation"]);
	}

	[Theory]
	[InlineData("../../other.vcdbs")]
	[InlineData("C:\\data\\Saves\\..")]
	[InlineData("/data/Saves/")]
	[InlineData("/data/Saves/world?.vcdbs")]
	[InlineData("/data/Saves/world|name.vcdbs")]
	public void Небезопасное_имя_мира_отвергается(string source)
	{
		var config = new JsonObject { ["WorldConfig"] = new JsonObject { ["SaveFileLocation"] = source } };
		Assert.False(ServerConfigPaths.Create("/target/Saves", "/target/Mods").Rebase(config).IsSuccess);
	}

	[Theory]
	[InlineData("/srv/main/data/Saves", "/srv/main/data/Mods", "default.vcdbs")]
	[InlineData("C:\\ProgramData\\servers\\main\\data\\Saves", "C:\\ProgramData\\servers\\main\\data\\Mods", "Мир.vcdbs")]
	public void Локальные_пути_становятся_переносимыми_и_возвращаются(string saves, string mods, string worldName)
	{
		var paths = ServerConfigPaths.Create(saves, mods);
		var originalWorld = saves + (saves.Contains('\\') ? "\\" : "/") + worldName;
		var config = new JsonObject
		{
			["WorldConfig"] = new JsonObject { ["SaveFileLocation"] = originalWorld },
			["ModPaths"] = new JsonArray("Mods", mods),
			["Description"] = "без изменений",
		};

		var portable = paths.Portable(config);
		Assert.True(portable.IsSuccess, portable.Failure?.ToString());
		Assert.Equal("Saves/" + worldName, (string?) portable.Value["WorldConfig"]!["SaveFileLocation"]);
		Assert.Equal("Mods", (string?) portable.Value["ModPaths"]![1]);
		Assert.Equal(originalWorld, (string?) config["WorldConfig"]!["SaveFileLocation"]);
		var rebased = paths.Rebase(portable.Value);
		Assert.True(rebased.IsSuccess, rebased.Failure?.ToString());
		Assert.Equal("без изменений", (string?) rebased.Value["Description"]);
		Assert.Equal(mods, (string?) rebased.Value["ModPaths"]!.AsArray().Last());
	}

	[Fact]
	public void Чужие_абсолютные_пути_не_попадают_в_пакет()
	{
		var paths = ServerConfigPaths.Create("/srv/main/data/Saves", "/srv/main/data/Mods");
		var config = new JsonObject { ["WorldConfig"] = new JsonObject { ["SaveFileLocation"] = "/srv/other/Saves/world.vcdbs" } };
		Assert.False(paths.Portable(config).IsSuccess);
		config["WorldConfig"]!["SaveFileLocation"] = "/srv/main/data/Saves/world.vcdbs";
		config["ModPaths"] = new JsonArray("/srv/other/Mods");
		Assert.False(paths.Portable(config).IsSuccess);
	}

	[Theory]
	[InlineData("../other/Mods")]
	[InlineData("Mods/../../other")]
	[InlineData("\\\\host\\share\\Mods")]
	[InlineData("C:Mods")]
	[InlineData("Mods//other")]
	public void Пути_модов_вне_сервера_отвергаются(string path)
	{
		var config = new JsonObject { ["ModPaths"] = new JsonArray(path) };
		Assert.False(ServerConfigPaths.Create("/srv/main/data/Saves", "/srv/main/data/Mods").Portable(config).IsSuccess);
	}
}
