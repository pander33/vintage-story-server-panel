using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Panel.Agent.Content;
using Panel.Content.Domain;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Tests;

public sealed class ServerPackageWriterTests : IDisposable
{
	private readonly string directory = Path.Combine("package-writer-tests", Guid.NewGuid().ToString("n"));

	public ServerPackageWriterTests() => Directory.CreateDirectory(directory);

	public void Dispose() => Directory.Delete(directory, recursive: true);

	[Fact]
	public async Task Export_creates_verified_package_without_backup_history()
	{
		var server = CreateServer();
		File.WriteAllText(Path.Combine(server.WorldDirectory, "active.vcdbs"), "world");
		var archive = Path.Combine(directory, "transfer.tar.gz");

		var result = await new BackupBackend(TimeProvider.System).ExportAsync(server, archive, CancellationToken.None);

		Assert.True(result.IsSuccess, result.Failure?.Message);
		Assert.Equal(new FileInfo(archive).Length, result.Value);
		Assert.True((await TreeArchive.JudgeTransferAsync(archive, CancellationToken.None)).IsSuccess);
		var inspected = await TreeArchive.InspectTransferAsync(archive, CancellationToken.None);
		Assert.True(inspected.IsSuccess, inspected.Failure?.Message);
		Assert.Equal("data/Saves/active.vcdbs", inspected.Value.ActiveWorld);
		Assert.Equal(64, inspected.Value.ManifestSha256.Length);
		Assert.Equal("1.22.7", inspected.Value.GameVersion);
	}

	[Fact]
	public async Task Failed_export_leaves_no_staging_file()
	{
		var server = CreateServer();
		var archive = Path.Combine(directory, "transfer.tar.gz");

		var result = await new BackupBackend(TimeProvider.System).ExportAsync(server, archive, CancellationToken.None);

		Assert.False(result.IsSuccess);
		Assert.False(File.Exists(archive));
	}

	[Fact]
	public async Task Mods_folder_contents_other_than_mod_files_are_ignored()
	{
		var server = CreateServer();
		File.WriteAllText(Path.Combine(server.WorldDirectory, "active.vcdbs"), "world");
		File.WriteAllText(Path.Combine(server.ModsDirectory, "demo.zip"), "mod");
		File.WriteAllText(Path.Combine(server.ModsDirectory, "notes.txt"), "text");
		Directory.CreateDirectory(Path.Combine(server.ModsDirectory, ".history"));
		File.WriteAllText(Path.Combine(server.ModsDirectory, ".history", "old.zip"), "old");
		var archive = Path.Combine(directory, "mods.tar.gz");

		var result = await ServerPackageWriter.WriteAsync(server, archive, CancellationToken.None);

		Assert.True(result.IsSuccess, result.Failure?.Message);
		var files = await ReadAsync(archive);
		Assert.Contains("data/Mods/demo.zip", files.Keys);
		Assert.DoesNotContain(files.Keys, name => name.Contains("history") || name.EndsWith("notes.txt", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Missing_mods_folder_is_not_an_error()
	{
		var server = CreateServer();
		File.WriteAllText(Path.Combine(server.WorldDirectory, "active.vcdbs"), "world");
		Directory.Delete(server.ModsDirectory);
		var archive = Path.Combine(directory, "nomods.tar.gz");

		var result = await ServerPackageWriter.WriteAsync(server, archive, CancellationToken.None);

		Assert.True(result.IsSuccess, result.Failure?.Message);
	}

	[Fact]
	public async Task Writes_only_portable_configuration_active_world_and_mods()
	{
		var server = CreateServer();
		File.WriteAllText(server.ConfigFile, new JsonObject
		{
			["ServerName"] = "My server",
			["Password"] = "secret",
			["StartupCommands"] = new JsonArray("secret command"),
			["UnknownSecret"] = "secret",
			["WorldConfig"] = new JsonObject
			{
				["SaveFileLocation"] = Path.Combine(server.WorldDirectory, "active.vcdbs"),
				["WorldName"] = "World",
				["UnknownSecret"] = "secret",
			},
			["ModPaths"] = new JsonArray(server.ModsDirectory),
		}.ToJsonString());
		File.WriteAllText(Path.Combine(server.WorldDirectory, "active.vcdbs"), "world");
		File.WriteAllText(Path.Combine(server.WorldDirectory, "other.vcdbs"), "private");
		File.WriteAllText(Path.Combine(server.ModsDirectory, "demo.zip"), "mod");
		File.WriteAllText(Path.Combine(server.ModsDirectory, "secret.txt"), "private");
		File.WriteAllText(Path.Combine(server.DataDirectory, "playerdata.json"), "private");
		var archive = Path.Combine(directory, "server.tar.gz");

		var result = await ServerPackageWriter.WriteAsync(server, archive, CancellationToken.None);

		Assert.True(result.IsSuccess, result.Failure?.Message);
		Assert.Equal(4, result.Value);
		var files = await ReadAsync(archive);
		Assert.Equal(["data/Mods/demo.zip", "data/Saves/active.vcdbs", "data/serverconfig.json", "vs-panel-server.json"],
			files.Keys.OrderBy(key => key, StringComparer.Ordinal));
		var manifest = ServerTransferManifest.Parse(files[ServerTransferArchive.Manifest]);
		Assert.True(manifest.IsSuccess, manifest.Failure?.Message);
		Assert.Equal("data/Saves/active.vcdbs", manifest.Value.ActiveWorld);
		Assert.True(manifest.Value.Verify(files.Where(file => file.Key != ServerTransferArchive.Manifest)
			.Select(file => new TransferFile(file.Key, file.Value.Length,
				Convert.ToHexStringLower(SHA256.HashData(file.Value)))).ToArray()).IsSuccess);
		var config = JsonNode.Parse(files["data/serverconfig.json"])!.AsObject();
		Assert.Equal("Saves/active.vcdbs", (string?) config["WorldConfig"]?["SaveFileLocation"]);
		Assert.Equal("Mods", (string?) config["ModPaths"]?[0]);
		Assert.Null(config["Password"]);
		Assert.Null(config["StartupCommands"]);
		Assert.Null(config["UnknownSecret"]);
		Assert.Null(config["WorldConfig"]?["UnknownSecret"]);
		Assert.Contains("secret", File.ReadAllText(server.ConfigFile));
		Assert.True((await TreeArchive.JudgeTransferAsync(archive, CancellationToken.None)).IsSuccess);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Rejects_missing_or_empty_active_world(bool empty)
	{
		var server = CreateServer();
		if (empty)
		{
			File.WriteAllBytes(Path.Combine(server.WorldDirectory, "active.vcdbs"), []);
		}
		var archive = Path.Combine(directory, "server.tar.gz");
		Assert.False((await ServerPackageWriter.WriteAsync(server, archive, CancellationToken.None)).IsSuccess);
		Assert.False(File.Exists(archive));
	}

	[Fact]
	public async Task Rejects_unsafe_mod_name_before_creating_archive()
	{
		var server = CreateServer();
		File.WriteAllText(Path.Combine(server.WorldDirectory, "active.vcdbs"), "world");
		File.WriteAllText(Path.Combine(server.ModsDirectory, "CON.zip"), "mod");
		var archive = Path.Combine(directory, "server.tar.gz");
		Assert.False((await ServerPackageWriter.WriteAsync(server, archive, CancellationToken.None)).IsSuccess);
		Assert.False(File.Exists(archive));
	}

	[Fact]
	public async Task Symlink_in_mod_directory_is_left_out()
	{
		var server = CreateServer();
		File.WriteAllText(Path.Combine(server.WorldDirectory, "active.vcdbs"), "world");
		var target = Path.Combine(directory, "target.zip");
		File.WriteAllText(target, "mod");
		try
		{
			File.CreateSymbolicLink(Path.Combine(server.ModsDirectory, "link.zip"), target);
		}
		catch (IOException) when (OperatingSystem.IsWindows())
		{
			return; // Создание ссылки требует права SeCreateSymbolicLinkPrivilege или режима разработчика.
		}
		var archive = Path.Combine(directory, "server.tar.gz");
		var result = await ServerPackageWriter.WriteAsync(server, archive, CancellationToken.None);
		Assert.True(result.IsSuccess, result.Failure?.Message);
		Assert.DoesNotContain("data/Mods/link.zip", (await ReadAsync(archive)).Keys);
	}

	private RegisteredServer CreateServer()
	{
		var data = Path.Combine(directory, "data");
		var saves = Path.Combine(data, "Saves");
		var mods = Path.Combine(data, "Mods");
		Directory.CreateDirectory(saves);
		Directory.CreateDirectory(mods);
		File.WriteAllText(Path.Combine(data, "serverconfig.json"), new JsonObject
		{
			["WorldConfig"] = new JsonObject { ["SaveFileLocation"] = Path.Combine(saves, "active.vcdbs") },
		}.ToJsonString());
		return new RegisteredServer
		{
			Id = InstanceId.Parse("example"),
			Name = "Example",
			Unit = "vs-example",
			GameVersion = "1.22.7",
			DataDirectory = data,
			WorldDirectory = saves,
			ModsDirectory = mods,
			ConfigFile = Path.Combine(data, "serverconfig.json"),
		};
	}

	private static async Task<Dictionary<string, byte[]>> ReadAsync(string archive)
	{
		var files = new Dictionary<string, byte[]>();
		await using var stream = File.OpenRead(archive);
		await using var gzip = new GZipStream(stream, CompressionMode.Decompress);
		await using var reader = new TarReader(gzip);
		while (await reader.GetNextEntryAsync() is { } entry)
		{
			using var contents = new MemoryStream();
			await entry.DataStream!.CopyToAsync(contents);
			files.Add(entry.Name, contents.ToArray());
		}
		return files;
	}
}
