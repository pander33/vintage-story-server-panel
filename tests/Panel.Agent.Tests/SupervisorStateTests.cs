using System.Text.Json.Nodes;
using Panel.Agent.Supervision;

namespace Panel.Agent.Tests;

/// <summary>
/// Формат файла состояния супервизора заморожен: служба после обновления находит уже
/// запущенный сервер.
/// </summary>
public sealed class SupervisorStateTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "vs-run-" + Guid.NewGuid().ToString("n"));

	public void Dispose()
	{
		if (Directory.Exists(directory))
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	/// <summary>Файл состояния в замороженном формате.</summary>
	[Fact]
	public void Файл_состояния_читается()
	{
		Directory.CreateDirectory(directory);
		var path = SupervisorState.PathFor(directory, "main");
		File.WriteAllText(path, """
			{
			  "pid": 4242,
			  "created": 1790000000.5625,
			  "started_at": 1790000001.25,
			  "log": "C:\\ProgramData\\vs-panel\\servers\\main\\data\\Logs\\panel-console.log",
			  "restarts": 3
			}
			""");

		var state = SupervisorState.Read(path);

		Assert.Equal(4242, state.Pid);
		Assert.Equal(1790000000.5625, state.Created);
		Assert.Equal(1790000001.25, state.StartedAt);
		Assert.Equal(3, state.Restarts);
		Assert.EndsWith("panel-console.log", state.Log);
	}

	[Fact]
	public async Task Запись_сохраняет_известные_и_чужие_ключи()
	{
		var path = SupervisorState.PathFor(directory, "main");
		Directory.CreateDirectory(directory);
		File.WriteAllText(path, """{"pid": 1, "created": 2, "note": "оставить"}""");

		var written = await (SupervisorState.Read(path) with { Pid = 0, Restarts = 5 }).WriteAsync(path, CancellationToken.None);

		var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
		Assert.True(written.IsSuccess);
		Assert.Equal(0, (int) document["pid"]!);
		Assert.Equal(2d, (double) document["created"]!);
		Assert.Equal(5, (int) document["restarts"]!);
		Assert.Equal("оставить", (string?) document["note"]);
		Assert.True(document.ContainsKey("started_at"));
		Assert.True(document.ContainsKey("log"));
	}

	[Theory]
	[InlineData("")]
	[InlineData("[]")]
	[InlineData("не json")]
	public void Нечитаемый_файл_это_пустое_состояние(string content)
	{
		Directory.CreateDirectory(directory);
		var path = SupervisorState.PathFor(directory, "main");
		File.WriteAllText(path, content);

		Assert.Equal(0, SupervisorState.Read(path).Pid);
	}

	[Fact]
	public void Отсутствующий_файл_это_пустое_состояние() =>
		Assert.Equal(new SupervisorState(), SupervisorState.Read(SupervisorState.PathFor(directory, "nope")));

	/// <summary>Pid переиспользуется, поэтому процесс считается своим только при совпадении времени создания.</summary>
	[Theory]
	[InlineData(1000.0, true)]
	[InlineData(1001.9, true)]
	[InlineData(998.1, true)]
	[InlineData(1002.0, false)]
	[InlineData(0.0, false)]
	public void Процесс_узнаётся_по_pid_и_времени_создания(double actual, bool same)
	{
		var state = new SupervisorState { Pid = 77, Created = 1000 };

		Assert.Equal(same, state.IsSameProcess(actual));
	}

	[Fact]
	public void Нулевой_pid_никогда_не_свой() =>
		Assert.False(new SupervisorState { Pid = 0, Created = 1000 }.IsSameProcess(1000));
}
