using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Panel.Agent.Content;
using Panel.Agent.Supervision;
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
