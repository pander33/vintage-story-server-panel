using Panel.Cli;

namespace Panel.Cli.Tests;

/// <summary>
/// Шаг «Каталог игровых серверов»: явный выбор, вопрос при первой установке, повтор после
/// отказа. Путь проверяет демон, здесь его заменяет ответ <c>manage</c>.
/// </summary>
public sealed class ServersRootStepTests : IDisposable
{
	private readonly string config = Path.Combine(Path.GetTempPath(), "vs-panel-" + Guid.NewGuid().ToString("n") + ".json");

	private readonly List<string> calls = [];

	private readonly List<string> warnings = [];

	private readonly StringWriter output = new();

	public ServersRootStepTests() =>
		File.WriteAllText(this.config, """{"version": 2, "layout": "windows", "instances_file": "C:\\x.json", "instances_root": "C:\\ProgramData\\vs-panel\\servers"}""");

	public void Dispose()
	{
		File.Delete(this.config);
		this.output.Dispose();
	}

	private readonly List<string> browsed = [];

	private ServersRootStep.Terminal Terminal(string? input) =>
		new(this.output, input is null ? null : new StringReader(input), _ => { }, this.warnings.Add);

	/// <summary>Терминал с окном выбора, которое по очереди возвращает заданные каталоги.</summary>
	private ServersRootStep.Terminal Browsing(params FolderPick[] picks)
	{
		var queue = new Queue<FolderPick>(picks);
		return Terminal(string.Empty) with
		{
			Browse = start =>
			{
				this.browsed.Add(start);
				return Task.FromResult(queue.Dequeue());
			},
		};
	}

	private Func<IReadOnlyList<string>, Task<bool>> Manage(params bool[] answers)
	{
		var queue = new Queue<bool>(answers);
		return command =>
		{
			this.calls.Add(string.Join(' ', command));
			return Task.FromResult(queue.Dequeue());
		};
	}

	[Fact]
	public async Task Явный_каталог_передаётся_демону_без_вопроса()
	{
		var options = new InstallOptions { ServersDirectory = @"D:\vs" };

		var ready = await ServersRootStep.RunAsync(options, this.config, firstInstall: false, Terminal("не должно читаться"), Manage(true));

		Assert.True(ready);
		Assert.Equal([@"servers-root D:\vs"], this.calls);
	}

	/// <summary>Первый сервер не создаётся в другом каталоге, чем был указан.</summary>
	[Fact]
	public async Task Отвергнутый_явный_каталог_отменяет_первый_сервер()
	{
		var options = new InstallOptions { ServersDirectory = @"D:\Games" };

		var ready = await ServersRootStep.RunAsync(options, this.config, firstInstall: true, Terminal(null), Manage(false));

		Assert.False(ready);
		Assert.Single(this.warnings);
	}

	[Fact]
	public async Task Enter_оставляет_каталог_по_умолчанию()
	{
		var ready = await ServersRootStep.RunAsync(new InstallOptions(), this.config, firstInstall: true, Terminal("\n"), Manage());

		Assert.True(ready);
		Assert.Empty(this.calls);
		Assert.Contains(@"[C:\ProgramData\vs-panel\servers]", this.output.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task После_отказа_вопрос_повторяется()
	{
		var ready = await ServersRootStep.RunAsync(new InstallOptions(), this.config, firstInstall: true, Terminal("D:\\Games\n\"D:\\vs-servers\"\n"), Manage(false, true));

		Assert.True(ready);
		Assert.Equal([@"servers-root D:\Games", @"servers-root D:\vs-servers"], this.calls);
	}

	[Fact]
	public async Task Конец_ввода_оставляет_каталог_по_умолчанию()
	{
		var ready = await ServersRootStep.RunAsync(new InstallOptions(), this.config, firstInstall: true, Terminal("D:\\Games\n"), Manage(false));

		Assert.True(ready);
		Assert.Single(this.calls);
	}

	[Fact]
	public async Task Выбранный_в_окне_каталог_передаётся_демону()
	{
		var ready = await ServersRootStep.RunAsync(new InstallOptions(), this.config, firstInstall: true, Browsing(new FolderPick(true, @"D:\vs-servers")), Manage(true));

		Assert.True(ready);
		Assert.Equal([@"servers-root D:\vs-servers"], this.calls);
		Assert.Equal([@"C:\ProgramData\vs-panel\servers"], this.browsed);
	}

	[Fact]
	public async Task Отмена_в_окне_оставляет_каталог_по_умолчанию()
	{
		var ready = await ServersRootStep.RunAsync(new InstallOptions(), this.config, firstInstall: true, Browsing(new FolderPick(true, null)), Manage());

		Assert.True(ready);
		Assert.Empty(this.calls);
	}

	/// <summary>После отказа окно открывается снова с того же каталога.</summary>
	[Fact]
	public async Task После_отказа_окно_открывается_снова()
	{
		var terminal = Browsing(new FolderPick(true, @"D:\Games"), new FolderPick(true, @"D:\vs-servers"));

		var ready = await ServersRootStep.RunAsync(new InstallOptions(), this.config, firstInstall: true, terminal, Manage(false, true));

		Assert.True(ready);
		Assert.Equal([@"servers-root D:\Games", @"servers-root D:\vs-servers"], this.calls);
		Assert.Equal([@"C:\ProgramData\vs-panel\servers", @"D:\Games"], this.browsed);
	}

	[Fact]
	public async Task Без_окна_путь_вводится_текстом()
	{
		var terminal = Browsing(FolderPick.Unavailable) with { Input = new StringReader("D:\\vs\n") };

		var ready = await ServersRootStep.RunAsync(new InstallOptions(), this.config, firstInstall: true, terminal, Manage(true));

		Assert.True(ready);
		Assert.Equal([@"servers-root D:\vs"], this.calls);
	}

	[Theory]
	[InlineData(false, "D:\\vs\n")]
	[InlineData(true, null)]
	public async Task Обновление_и_установка_без_терминала_не_спрашивают(bool firstInstall, string? input)
	{
		var ready = await ServersRootStep.RunAsync(new InstallOptions(), this.config, firstInstall, Terminal(input), Manage());

		Assert.True(ready);
		Assert.Empty(this.calls);
		Assert.Empty(this.output.ToString());
	}
}
