using System.Text.Json;
using Panel.Content.Domain;

namespace Panel.Content.Tests;

public sealed class ServerTransferManifestTests
{
	private const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
	private const string world = "data/Saves/main.vcdbs";
	private static string Hash => hash;
	private static string World => world;

	private static string Json(string? world = ServerTransferManifestTests.world, object[]? files = null, int version = 1, string gameVersion = "1.22.7") => JsonSerializer.Serialize(new
	{
		version,
		active_world = world,
		game_version = gameVersion,
		files = files ?? [new { path = "data/serverconfig.json", length = 4L, sha256 = Hash },
			new { path = World, length = 8L, sha256 = Hash }],
	});

	[Fact]
	public void Версия_файлы_и_активный_мир_читаются_и_сверяются()
	{
		var manifest = ServerTransferManifest.Parse(Json()).Value;

		Assert.Equal(1, manifest.Version);
		Assert.Equal(World, manifest.ActiveWorld);
		Assert.Equal("1.22.7", manifest.GameVersion);
		Assert.True(manifest.Verify([new(World, 8, Hash), new("data/serverconfig.json", 4, Hash)]).IsSuccess);
	}

	[Fact]
	public void Создание_сериализация_и_чтение_байтов_сохраняют_проверенный_манифест()
	{
		var created = ServerTransferManifest.Create(World, [new("data/serverconfig.json", 4, Hash), new(World, 8, Hash)]);
		Assert.True(created.IsSuccess);
		var parsed = ServerTransferManifest.Parse(created.Value.Serialize());
		Assert.True(parsed.IsSuccess);
		Assert.Equal(created.Value.Files, parsed.Value.Files);
		Assert.Equal(World, parsed.Value.ActiveWorld);
		Assert.Equal(string.Empty, parsed.Value.GameVersion);
		Assert.Equal("1.21.3", ServerTransferManifest.Parse(ServerTransferManifest.Create(World,
			[new("data/serverconfig.json", 4, Hash), new(World, 8, Hash)], "1.21.3").Value.Serialize()).Value.GameVersion);
		Assert.False(ServerTransferManifest.Create(World, [new(World, 8, Hash)]).IsSuccess);
		Assert.False(ServerTransferManifest.Parse(new byte[] { 0xFF, 0xFE }).IsSuccess);
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("null")]
	[InlineData("[]")]
	[InlineData("{")]
	[InlineData("{\"version\":1,\"version\":1,\"active_world\":\"data/Saves/main.vcdbs\",\"files\":[]}")]
	[InlineData("{\"version\":true,\"active_world\":\"data/Saves/main.vcdbs\",\"files\":[]}")]
	public void Некорректный_json_и_повторные_ключи_отвергаются(string json) =>
		Assert.False(ServerTransferManifest.Parse(json).IsSuccess);

	[Theory]
	[InlineData("1.22 7")]
	[InlineData("../1")]
	[InlineData("1.22.7 ")]
	public void Недопустимая_версия_игры_отвергается(string version) =>
		Assert.False(ServerTransferManifest.Parse(Json(gameVersion: version)).IsSuccess);

	[Fact]
	public void Версия_игры_обязательна_но_может_быть_пустой()
	{
		Assert.True(ServerTransferManifest.Parse(Json(gameVersion: "")).IsSuccess);
		Assert.False(ServerTransferManifest.Parse(Json().Replace("\"game_version\":\"1.22.7\",", "", StringComparison.Ordinal)).IsSuccess);
		Assert.False(ServerTransferManifest.Parse(Json(gameVersion: new string('1', ServerTransferManifest.MaximumGameVersionLength + 1))).IsSuccess);
	}

	[Fact]
	public void Неизвестные_ключи_и_секреты_не_допускаются()
	{
		Assert.False(ServerTransferManifest.Parse(Json()[..^1] + ",\"password\":\"secret\"}").IsSuccess);
		Assert.False(ServerTransferManifest.Parse(Json().Replace("\"sha256\":", "\"token\":\"secret\",\"sha256\":", StringComparison.Ordinal)).IsSuccess);
		Assert.False(ServerTransferManifest.Parse(Json().Replace("\"length\":4", "\"length\":4,\"length\":4", StringComparison.Ordinal)).IsSuccess);
	}

	[Fact]
	public void Несовместимая_версия_и_превышенный_размер_отвергаются()
	{
		Assert.False(ServerTransferManifest.Parse(Json(version: 2)).IsSuccess);
		Assert.False(ServerTransferManifest.Parse(Json() + new string(' ', ServerTransferManifest.MaximumManifestBytes)).IsSuccess);
	}

	[Theory]
	[InlineData("data/Mods/../a.zip")]
	[InlineData("data/Mods/a\\b.zip")]
	[InlineData("data/Mods/CON.zip")]
	[InlineData("vs-panel-server.json")]
	public void Небезопасный_путь_файла_отвергается(string path) =>
		Assert.False(ServerTransferManifest.Parse(Json(files: [new { path = "data/serverconfig.json", length = 4L, sha256 = Hash },
			new { path = World, length = 8L, sha256 = Hash }, new { path, length = 1L, sha256 = Hash }])).IsSuccess);

	[Fact]
	public void Коллизии_путей_и_отсутствие_конфигурации_отвергаются()
	{
		Assert.False(ServerTransferManifest.Parse(Json(files: [new { path = "data/serverconfig.json", length = 4L, sha256 = Hash },
			new { path = "data/Saves/main.vcdbs", length = 8L, sha256 = Hash },
			new { path = "data/saves/MAIN.vcdbs", length = 8L, sha256 = Hash }])).IsSuccess);
		Assert.False(ServerTransferManifest.Parse(Json(files: [new { path = World, length = 8L, sha256 = Hash }])).IsSuccess);
	}

	[Fact]
	public void Активный_мир_должен_быть_непустым_файлом_из_списка()
	{
		Assert.False(ServerTransferManifest.Parse(Json(world: "data/Saves/missing.vcdbs")).IsSuccess);
		Assert.False(ServerTransferManifest.Parse(Json(world: "data/Saves/../main.vcdbs")).IsSuccess);
		Assert.False(ServerTransferManifest.Parse(Json(files: [new { path = "data/serverconfig.json", length = 4L, sha256 = Hash },
			new { path = World, length = 0L, sha256 = Hash }])).IsSuccess);
	}

	[Fact]
	public void Размеры_и_sha256_ограничены()
	{
		Assert.False(ServerTransferManifest.Parse(Json(files: [new { path = "data/serverconfig.json", length = (long) ServerTransferArchive.MaximumConfigBytes + 1, sha256 = Hash },
			new { path = World, length = 8L, sha256 = Hash }])).IsSuccess);
		foreach (var length in new[] { -1L, ServerTransferArchive.MaximumFileBytes + 1 })
		{
			Assert.False(ServerTransferManifest.Parse(Json(files: [new { path = "data/serverconfig.json", length, sha256 = Hash },
				new { path = World, length = 8L, sha256 = Hash }])).IsSuccess);
		}
		foreach (var invalidHash in new[] { "abc", new string('g', 64), Hash.ToUpperInvariant() })
		{
			Assert.False(ServerTransferManifest.Parse(Json(files: [new { path = "data/serverconfig.json", length = 4L, sha256 = invalidHash },
				new { path = World, length = 8L, sha256 = Hash }])).IsSuccess);
		}
	}

	[Fact]
	public void Проверка_отвергает_лишние_отсутствующие_и_подменённые_файлы()
	{
		var manifest = ServerTransferManifest.Parse(Json()).Value;
		Assert.False(manifest.Verify([new(World, 8, Hash)]).IsSuccess);
		Assert.False(manifest.Verify([new(World, 8, Hash), new("data/Mods/extra.zip", 4, Hash)]).IsSuccess);
		Assert.False(manifest.Verify([new(World, 8, Hash), new(World, 8, Hash)]).IsSuccess);
		Assert.False(manifest.Verify([new(World, 7, Hash), new("data/serverconfig.json", 4, Hash)]).IsSuccess);
		Assert.False(manifest.Verify([new(World, 8, new string('0', 64)), new("data/serverconfig.json", 4, Hash)]).IsSuccess);
	}
}
