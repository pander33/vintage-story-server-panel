using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Panel.Agent.Content;
using Panel.Agent.Supervision;
using Panel.Content.Domain;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Tests;

/// <summary>
/// Содержимое на Windows: копии, замер диска и установка сервера супервизором. На временных
/// каталогах, без игры и демона.
/// </summary>
public sealed class WindowsContentTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "vs-content-" + Guid.NewGuid().ToString("n"));

	public WindowsContentTests() => Directory.CreateDirectory(directory);

	public void Dispose()
	{
		if (Directory.Exists(directory))
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	/// <summary>Резервная копия: запись, сверка, распаковка на текущей платформе.</summary>
	[Fact]
	public async Task Копия_каталога_пишется_сверяется_и_распаковывается()
	{
		var saves = Path.Combine(directory, "data", "Saves");
		Directory.CreateDirectory(saves);
		File.WriteAllText(Path.Combine(saves, "default.vcdbs"), "мир");
		var archive = Path.Combine(directory, "backup.tar.gz");

		var written = await TreeArchive.WriteAsync(Path.Combine(directory, "data"), archive, CancellationToken.None);
		var inspected = await TreeArchive.InspectAsync(archive, CancellationToken.None);
		var restored = Path.Combine(directory, "restored");
		var extracted = await TreeArchive.ExtractAsync(archive, restored, CancellationToken.None);

		Assert.True(written.IsSuccess);
		Assert.Equal("default.vcdbs", Assert.Single(inspected.Value.Worlds).Name);
		Assert.True(extracted.IsSuccess);
		Assert.Equal("мир", File.ReadAllText(Path.Combine(restored, "data", "Saves", "default.vcdbs")));
	}

	[Fact]
	public async Task Переносимая_копия_проверяется_перед_распаковкой()
	{
		var data = Path.Combine(directory, "data");
		Directory.CreateDirectory(Path.Combine(data, "Saves"));
		Directory.CreateDirectory(Path.Combine(data, "Mods"));
		File.WriteAllText(Path.Combine(data, "serverconfig.json"),
			new JsonObject { ["WorldConfig"] = new JsonObject { ["SaveFileLocation"] = Path.Combine(data, "Saves", "default.vcdbs") } }.ToJsonString());
		File.WriteAllText(Path.Combine(data, "Saves", "default.vcdbs"), "мир");
		var archive = Path.Combine(directory, "transfer.tar.gz");
		Assert.True((await ServerPackageWriter.WriteAsync(Server("supervised") with
		{
			WorldDirectory = Path.Combine(data, "Saves"),
			ModsDirectory = Path.Combine(data, "Mods"),
			ConfigFile = Path.Combine(data, "serverconfig.json"),
			DataDirectory = data,
		}, archive, CancellationToken.None)).IsSuccess);

		var destination = Path.Combine(directory, "restored");
		Assert.True((await TreeArchive.JudgeTransferAsync(archive, CancellationToken.None)).IsSuccess);
		Assert.True((await TreeArchive.ExtractTransferAsync(archive, destination, CancellationToken.None)).IsSuccess);
		Assert.Equal("мир", File.ReadAllText(Path.Combine(destination, "data", "Saves", "default.vcdbs")));
		Assert.False((await TreeArchive.ExtractTransferAsync(archive, destination, CancellationToken.None)).IsSuccess);
		Assert.Equal("мир", File.ReadAllText(Path.Combine(destination, "data", "Saves", "default.vcdbs")));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Пакет_без_целого_активного_мира_не_распаковывается(bool emptyWorld)
	{
		var data = Path.Combine(directory, "data");
		Directory.CreateDirectory(Path.Combine(data, "Saves"));
		File.WriteAllText(Path.Combine(data, "serverconfig.json"), """{"WorldConfig":{"SaveFileLocation":"Saves/default.vcdbs"}}""");
		if (emptyWorld)
		{
			File.WriteAllBytes(Path.Combine(data, "Saves", "default.vcdbs"), []);
		}

		var archive = Path.Combine(directory, "transfer.tar.gz");
		Assert.True((await TreeArchive.WriteAsync(data, archive, CancellationToken.None)).IsSuccess);
		var destination = Path.Combine(directory, "restored");
		Assert.False((await TreeArchive.ExtractTransferAsync(archive, destination, CancellationToken.None)).IsSuccess);
		Assert.False(Directory.Exists(destination));
	}

	[Fact]
	public async Task Копия_без_конфигурации_не_создаёт_каталог_назначения()
	{
		var data = Path.Combine(directory, "data");
		Directory.CreateDirectory(data);
		File.WriteAllText(Path.Combine(data, "other.txt"), "не переносить");
		var archive = Path.Combine(directory, "transfer.tar.gz");
		Assert.True((await TreeArchive.WriteAsync(data, archive, CancellationToken.None)).IsSuccess);

		var destination = Path.Combine(directory, "restored");
		Assert.False((await TreeArchive.ExtractTransferAsync(archive, destination, CancellationToken.None)).IsSuccess);
		Assert.False(Directory.Exists(destination));
	}

	[Fact]
	public async Task Обрезанный_архив_отвергается_до_распаковки()
	{
		var data = Path.Combine(directory, "data");
		Directory.CreateDirectory(data);
		File.WriteAllText(Path.Combine(data, "serverconfig.json"), "{}");
		var archive = Path.Combine(directory, "transfer.tar.gz");
		Assert.True((await TreeArchive.WriteAsync(data, archive, CancellationToken.None)).IsSuccess);
		using (var file = new FileStream(archive, FileMode.Open, FileAccess.Write))
		{
			file.SetLength(file.Length - 8);
		}

		var destination = Path.Combine(directory, "restored");
		Assert.False((await TreeArchive.ExtractTransferAsync(archive, destination, CancellationToken.None)).IsSuccess);
		Assert.False(Directory.Exists(destination));
	}

	[Fact]
	public async Task Неверная_контрольная_сумма_gzip_отвергается_до_распаковки()
	{
		var data = Path.Combine(directory, "data");
		Directory.CreateDirectory(data);
		File.WriteAllText(Path.Combine(data, "serverconfig.json"), "{}");
		var archive = Path.Combine(directory, "transfer.tar.gz");
		Assert.True((await TreeArchive.WriteAsync(data, archive, CancellationToken.None)).IsSuccess);
		using (var file = new FileStream(archive, FileMode.Open, FileAccess.ReadWrite))
		{
			file.Position = file.Length - 8;
			var first = file.ReadByte();
			file.Position--;
			file.WriteByte((byte) (first ^ 1));
		}

		var destination = Path.Combine(directory, "restored");
		Assert.False((await TreeArchive.ExtractTransferAsync(archive, destination, CancellationToken.None)).IsSuccess);
		Assert.False(Directory.Exists(destination));
	}

	[Fact]
	public async Task Восстановление_копии_переносит_пути_в_настройках()
	{
		if (!GameOwnership.Possible)
		{
			return;
		}

		var root = Path.Combine(directory, "servers", "creative");
		var server = Server("supervised") with
		{
			WorldDirectory = Path.Combine(root, "data", "Saves"),
			ModsDirectory = Path.Combine(root, "data", "Mods"),
			BackupDirectory = Path.Combine(root, "backups"),
			ConfigFile = Path.Combine(root, "data", "serverconfig.json"),
		};
		Directory.CreateDirectory(server.WorldDirectory);
		Directory.CreateDirectory(server.ModsDirectory);
		Directory.CreateDirectory(server.BackupDirectory);
		File.WriteAllText(Path.Combine(server.DataDirectory, "serverconfig.json"), new JsonObject
		{
			["Password"] = "local-secret",
			["WorldConfig"] = new JsonObject { ["SaveFileLocation"] = Path.Combine(server.WorldDirectory, "old.vcdbs") },
		}.ToJsonString());
		File.WriteAllText(Path.Combine(server.WorldDirectory, "old.vcdbs"), "прежний мир");
		File.WriteAllText(Path.Combine(server.DataDirectory, "original.txt"), "старые данные");
		var foreignData = Path.Combine(directory, "foreign", "data");
		Directory.CreateDirectory(Path.Combine(foreignData, "Saves"));
		Directory.CreateDirectory(Path.Combine(foreignData, "Mods"));
		File.WriteAllText(Path.Combine(foreignData, "serverconfig.json"), new JsonObject
		{
			["WorldConfig"] = new JsonObject { ["SaveFileLocation"] = Path.Combine(foreignData, "Saves", "default.vcdbs") },
		}.ToJsonString());
		File.WriteAllText(Path.Combine(foreignData, "Saves", "default.vcdbs"), "мир");
		var archive = Path.Combine(server.BackupDirectory, "vintagestory-foreign.tar.gz");
		Assert.True((await ServerPackageWriter.WriteAsync(server with
		{
			DataDirectory = foreignData,
			WorldDirectory = Path.Combine(foreignData, "Saves"),
			ModsDirectory = Path.Combine(foreignData, "Mods"),
			ConfigFile = Path.Combine(foreignData, "serverconfig.json"),
		}, archive, CancellationToken.None)).IsSuccess);

		var result = await new BackupBackend(TimeProvider.System)
			.RestoreAsync(server, BackupName.Create("vintagestory-foreign.tar.gz").Value, CancellationToken.None);

		Assert.True(result.IsSuccess, result.Failure?.ToString());
		var config = JsonNode.Parse(File.ReadAllText(Path.Combine(server.DataDirectory, "serverconfig.json")))!;
		Assert.Equal(Path.Combine(server.WorldDirectory, "default.vcdbs"), (string?) config["WorldConfig"]!["SaveFileLocation"]);
		Assert.Equal(server.ModsDirectory, (string?) config["ModPaths"]![1]);
		Assert.Equal("local-secret", (string?) config["Password"]);
		Assert.Equal("старые данные", File.ReadAllText(Path.Combine(server.DataDirectory, "original.txt")));
		Assert.True(File.Exists(Path.Combine(server.BackupDirectory, result.Value.Value)));
	}

	[Fact]
	public async Task Восстановление_сохраняет_локальные_файлы_вне_пакета()
	{
		if (!GameOwnership.Possible)
		{
			return;
		}

		var root = Path.Combine(directory, "servers", "creative");
		var server = Server("supervised") with
		{
			WorldDirectory = Path.Combine(root, "data", "Saves"),
			ModsDirectory = Path.Combine(root, "data", "Mods"),
			BackupDirectory = Path.Combine(root, "backups"),
			ConfigFile = Path.Combine(root, "data", "serverconfig.json"),
		};
		Directory.CreateDirectory(server.WorldDirectory);
		Directory.CreateDirectory(server.ModsDirectory);
		Directory.CreateDirectory(server.BackupDirectory);
		File.WriteAllText(Path.Combine(server.WorldDirectory, "old.vcdbs"), "прежний мир");
		File.WriteAllText(server.ConfigFile, new JsonObject
		{
			["WorldConfig"] = new JsonObject { ["SaveFileLocation"] = Path.Combine(server.WorldDirectory, "old.vcdbs") },
		}.ToJsonString());
		var package = await new BackupBackend(TimeProvider.System).CreateAsync(server, "vintagestory-", CancellationToken.None);
		Assert.True(package.IsSuccess, package.Failure?.ToString());

		Directory.CreateDirectory(Path.Combine(server.DataDirectory, "other"));
		File.WriteAllText(Path.Combine(server.DataDirectory, "other", "record"), "сохранить");
		var restored = await new BackupBackend(TimeProvider.System).RestoreAsync(server, package.Value.Name, CancellationToken.None);
		Assert.True(restored.IsSuccess, restored.Failure?.ToString());
		Assert.Equal("сохранить", File.ReadAllText(Path.Combine(server.DataDirectory, "other", "record")));
	}

	[Fact]
	public async Task Новая_резервная_копия_содержит_манифест_и_мир_без_пароля()
	{
		var root = Path.Combine(directory, "servers", "main", "data");
		var server = Server("supervised") with
		{
			DataDirectory = root,
			WorldDirectory = Path.Combine(root, "Saves"),
			ModsDirectory = Path.Combine(root, "Mods"),
			ConfigFile = Path.Combine(root, "serverconfig.json"),
			BackupDirectory = Path.Combine(directory, "backups"),
		};
		Directory.CreateDirectory(server.WorldDirectory);
		Directory.CreateDirectory(server.ModsDirectory);
		File.WriteAllText(Path.Combine(server.WorldDirectory, "main.vcdbs"), "текущий мир");
		File.WriteAllText(server.ConfigFile, new JsonObject
		{
			["Password"] = "local-secret",
			["WorldConfig"] = new JsonObject { ["SaveFileLocation"] = Path.Combine(server.WorldDirectory, "main.vcdbs") },
		}.ToJsonString());
		var backups = new BackupBackend(TimeProvider.System);
		var created = await backups.CreateAsync(server, "vintagestory-", CancellationToken.None);
		Assert.True(created.IsSuccess, created.Failure?.ToString());
		var inspected = await backups.InspectAsync(server, created.Value.Name, CancellationToken.None);
		Assert.True(inspected.IsSuccess, inspected.Failure?.ToString());
		Assert.Equal("main.vcdbs", Assert.Single(inspected.Value.Worlds).Name);
		var destination = Path.Combine(directory, "unpacked");
		Assert.True((await TreeArchive.ExtractTransferAsync(
			Path.Combine(server.BackupDirectory, created.Value.Name.Value), destination, CancellationToken.None)).IsSuccess);
		Assert.Equal("текущий мир", File.ReadAllText(Path.Combine(destination, "data", "Saves", "main.vcdbs")));
		Assert.DoesNotContain("local-secret", File.ReadAllText(Path.Combine(destination, "data", "serverconfig.json")));
	}

	[Theory]
	[InlineData(@"C:\ProgramData\vs-panel", @"C:\", true)]
	[InlineData(@"c:\programdata", @"C:\", true)]
	[InlineData(@"D:\Games", @"C:\", false)]
	[InlineData(@"C:\", @"C:\", true)]
	public void Том_Windows_узнаётся_без_учёта_регистра(string path, string mount, bool expected)
	{
		Assert.Equal(expected, GameFiles.IsUnder(path, mount, windows: true));
	}

	[Theory]
	[InlineData("/srv/vs-panel", "/srv", true)]
	[InlineData("/srvx", "/srv", false)]
	[InlineData("/var", "/", true)]
	public void Точка_монтирования_Linux_по_префиксу(string path, string mount, bool expected)
	{
		Assert.Equal(expected, GameFiles.IsUnder(path, mount, windows: false));
	}

	[Fact]
	public void Место_на_диске_каталога_известно()
	{
		var usage = GameFiles.Usage(directory);

		Assert.True(usage.Total > 0);
		Assert.True(usage.Free > 0);
	}

	/// <summary>Служба ОС не ставится, создаётся файл состояния с пустым pid.</summary>
	[Fact]
	[SupportedOSPlatform("windows")]
	public async Task Установка_супервизором_заводит_состояние_без_процесса()
	{
		if (!OperatingSystem.IsWindows() || SupervisedBackend.DotnetPath() is null)
		{
			return;
		}

		var server = Server("supervised");
		Directory.CreateDirectory(server.ProgramDirectory);
		File.WriteAllText(Path.Combine(server.ProgramDirectory, "VintagestoryServer.dll"), "dll");
		var run = Path.Combine(directory, "run");
		var supervisor = new SupervisedBackend(run);

		var adopted = await supervisor.AdoptAsync(server, CancellationToken.None);

		Assert.True(adopted.IsSuccess);
		var state = JsonNode.Parse(File.ReadAllText(adopted.Value))!.AsObject();
		Assert.Equal(0, (int) state["pid"]!);
		Assert.Equal(Path.Combine(run, "creative.console.log"), (string?) state["log"]);
		Assert.False(await supervisor.IsActiveAsync(server, CancellationToken.None));
	}

	[Fact]
	[SupportedOSPlatform("windows")]
	public async Task Установка_супервизором_отказывает_без_программы_и_с_чужим_транспортом()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		var supervisor = new SupervisedBackend(Path.Combine(directory, "run"));

		var foreign = await supervisor.AdoptAsync(Server("fifo"), CancellationToken.None);
		var missing = await supervisor.AdoptAsync(Server("supervised"), CancellationToken.None);

		Assert.Equal(FailureCode.Conflict, foreign.Failure!.Code);
		Assert.False(missing.IsSuccess);
	}

	/// <summary>Удаление незапущенного сервера: состояние удаляется, файлы остаются.</summary>
	[Fact]
	[SupportedOSPlatform("windows")]
	public async Task Снятие_незапущенного_сервера_удаляет_только_состояние()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		var server = Server("supervised");
		var run = Path.Combine(directory, "run");
		Directory.CreateDirectory(run);
		var statePath = SupervisorState.PathFor(run, "creative");
		File.WriteAllText(statePath, """{"pid": 0}""");
		Directory.CreateDirectory(server.DataDirectory);

		var forgotten = await new SupervisedBackend(run).ForgetAsync(server, TimeSpan.FromSeconds(5), CancellationToken.None);

		Assert.True(forgotten.IsSuccess);
		Assert.False(File.Exists(statePath));
		Assert.True(Directory.Exists(server.DataDirectory));
	}

	private RegisteredServer Server(string transport)
	{
		var root = Path.Combine(directory, "servers", "creative");
		return new RegisteredServer
		{
			Id = InstanceId.Parse("creative"),
			Name = "creative",
			Unit = "vs-creative",
			ProgramDirectory = Path.Combine(root, "server"),
			DataDirectory = Path.Combine(root, "data"),
			LogDirectory = Path.Combine(root, "data", "Logs"),
			CommandTransport = transport,
		};
	}
}
