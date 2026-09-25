using Panel.Cli;
using Panel.Fleet.Infrastructure;

namespace Panel.Cli.Tests;

/// <summary>
/// Установщик для Windows до обращения к диспетчеру служб: шаблоны, параметры, сборка с
/// <c>.exe</c>, командные строки служб, ссылка мастера, файловая часть <c>doctor</c>. Службы,
/// права входа и список доступа проверяются только на машине.
/// </summary>
public sealed class WindowsInstallTests : IDisposable
{
	private readonly string root = Path.Combine(Path.GetTempPath(), "vs-win-" + Guid.NewGuid().ToString("n"));

	private WindowsPaths Paths => new(Path.Combine(root, "Program Files", "vs-panel"), Path.Combine(root, "ProgramData", "vs-panel"));

	public void Dispose()
	{
		if (Directory.Exists(root))
		{
			Directory.Delete(root, recursive: true);
		}
	}

	/// <summary>Путь данных с обратными косыми чертами и кавычкой даёт корректный JSON с тем же путём.</summary>
	[Fact]
	public void Настройки_панели_Windows_с_настоящим_каталогом_данных()
	{
		const string data = @"E:\Data ""x""\vs-panel";

		var text = Templates.WindowsPanelConfig(data);
		var config = PanelConfiguration.Parse(text);

		Assert.True(config.IsSuccess, config.Failure?.Message);
		Assert.Equal(Panel.SharedKernel.PlatformLayout.Windows, config.Value.Layout);
		Assert.Equal(2, config.Value.Version);
		Assert.Equal(data + @"\instances.json", config.Value.InstancesFile);
		Assert.Equal(data + @"\servers", config.Value.InstancesRoot);
		Assert.Equal(data + @"\run", config.Value.RunDirectory);
		Assert.Equal(data + @"\vs-panel.env", config.Value.PanelEnvFile);
		Assert.DoesNotContain(Templates.DataRootPlaceholder, text, StringComparison.Ordinal);
	}

	[Fact]
	public void Окружение_панели_Windows_без_привилегированного_помощника()
	{
		var environment = EnvironmentFile.Parse(Templates.WindowsPanelEnvironment(@"C:\ProgramData\vs-panel"));

		Assert.Equal(@"C:\ProgramData\vs-panel\state", environment["PANEL_STATE_DIR"]);
		Assert.Equal("127.0.0.1", environment["PANEL_BIND"]);
		Assert.Equal("false", environment["PANEL_SECURE_COOKIE"]);
		Assert.DoesNotContain(environment.Keys, key => key.StartsWith("PANEL_HELPER", StringComparison.Ordinal));
	}

	/// <summary>
	/// Ключи окружения Windows те же, что у Linux, кроме каталога состояния: мастер пишет одни и те
	/// же ключи на обеих платформах.
	/// </summary>
	[Fact]
	public void Окружение_Windows_и_Linux_различаются_только_каталогом_состояния()
	{
		var windows = EnvironmentFile.Parse(Templates.WindowsPanelEnvironment(@"C:\ProgramData\vs-panel"));
		var linux = EnvironmentFile.Parse(Templates.PanelEnvironment);

		Assert.Equal(linux.Keys.Order(StringComparer.Ordinal), windows.Keys.Order(StringComparer.Ordinal));
		Assert.All(linux.Keys.Where(key => key != "PANEL_STATE_DIR"), key => Assert.Equal(linux[key], windows[key]));
	}

	/// <summary>cmd читает пакетный файл построчно, и с LF метки и переходы ломаются.</summary>
	[Fact]
	public void Первый_шаг_установки_Windows_с_CRLF()
	{
		var text = File.ReadAllText(Path.Combine(Repository(), "deploy", "windows", "install.cmd"));

		Assert.DoesNotContain("\r\r", text, StringComparison.Ordinal);
		Assert.Equal(text.Split('\n').Length - 1, text.Split("\r\n").Length - 1);
		Assert.Contains("install --from \"%~dp0.\"", text, StringComparison.Ordinal);
		Assert.Contains("-Verb RunAs", text, StringComparison.Ordinal);
	}

	[Fact]
	public void Параметры_установки_Windows()
	{
		var options = InstallOptions.ParseWindows(["--from", @"C:\build", "--no-browser"], new Dictionary<string, string>());

		Assert.True(options.IsSuccess);
		Assert.Equal(@"C:\build", options.Value.From);
		Assert.False(options.Value.OpenBrowser);
	}

	[Fact]
	public void По_умолчанию_браузер_открывается()
	{
		var options = InstallOptions.ParseWindows([], new Dictionary<string, string>()).Value;

		Assert.True(options.OpenBrowser);
		Assert.Equal(string.Empty, options.TelemetryModDirectory);
	}

	[Theory]
	[InlineData("--skip-packages")]
	[InlineData("--account", "system")]
	[InlineData("--service-password")]
	public void Чужие_и_неизвестные_параметры_на_Windows_отвергаются(params string[] arguments)
	{
		Assert.False(InstallOptions.ParseWindows(arguments, new Dictionary<string, string>()).IsSuccess);
	}

	[Theory]
	[InlineData("--no-browser")]
	public void Параметры_Windows_на_Linux_отвергаются(params string[] arguments)
	{
		Assert.False(InstallOptions.Parse(arguments, new Dictionary<string, string>(), InstallPaths.System).IsSuccess);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Сборка_Windows_открывается_с_exe_под_любыми_именами(bool installedNames)
	{
		var build = Directory.CreateDirectory(Path.Combine(root, "build")).FullName;
		foreach (var (published, installed) in Artifact.Binaries)
		{
			File.WriteAllText(Path.Combine(build, (installedNames ? installed : published) + ".exe"), "bin");
		}

		File.WriteAllText(Path.Combine(build, Artifact.VersionFile), "2.0.0\n");
		File.WriteAllText(Path.Combine(build, Artifact.RidFile), "win-x64\n");

		var artifact = Artifact.Open(build, windows: true);

		Assert.True(artifact.IsSuccess, artifact.Failure?.Message);
		Assert.Equal("win-x64", artifact.Value.Rid);
		Assert.All(Artifact.Binaries, binary => Assert.True(File.Exists(artifact.Value.Source(binary))));
		Assert.Equal("vs-panel-web.exe", artifact.Value.Installed(Artifact.Binaries[0]));
	}

	/// <summary>Сборка для Linux без <c>.exe</c> на Windows не принимается, и наоборот.</summary>
	[Fact]
	public void Сборка_Linux_не_годится_для_Windows()
	{
		var build = Directory.CreateDirectory(Path.Combine(root, "linux")).FullName;
		foreach (var (published, _) in Artifact.Binaries)
		{
			File.WriteAllText(Path.Combine(build, published), "bin");
		}

		File.WriteAllText(Path.Combine(build, Artifact.VersionFile), "2.0.0\n");

		var artifact = Artifact.Open(build, windows: true);

		Assert.False(artifact.IsSuccess);
		Assert.Contains("Panel.Web.exe", artifact.Failure!.Message, StringComparison.Ordinal);
		Assert.Contains("win-x64", artifact.Failure!.Message, StringComparison.Ordinal);
	}

	/// <summary>
	/// Путь службы в кавычках: без них путь с пробелом в <c>Program Files</c> даёт уязвимость
	/// «незакавыченного пути службы». Службы запускают <c>current</c>, а не каталог версии.
	/// </summary>
	[Fact]
	public void Службы_запускают_активную_версию_по_закавыченному_пути()
	{
		var paths = new WindowsPaths(@"C:\Program Files\vs-panel", @"C:\ProgramData\vs-panel");

		Assert.Equal(@"""C:\Program Files\vs-panel\current\vs-panel-web.exe""", paths.PanelCommandLine);
		Assert.Equal(
			@"""C:\Program Files\vs-panel\current\vs-panel-agent.exe"" serve --pipe vs-panel-agent --panel-config ""C:\ProgramData\vs-panel\vs-panel.json""",
			paths.AgentCommandLine);
	}

	/// <summary>Имена служб те, что ожидают демон (при перезапуске панели) и <c>UseWindowsService</c>.</summary>
	[Fact]
	public void Имена_служб_совпадают_с_ожидаемыми_демоном_и_панелью()
	{
		Assert.Equal("vs-panel", WindowsPaths.PanelService);
		Assert.Equal("vs-panel-agent", WindowsPaths.AgentService);
		Assert.Equal("vs-panel-agent", WindowsPaths.AgentPipe);
	}

	[Theory]
	[InlineData("LocalSystem", true)]
	[InlineData("localsystem", true)]
	[InlineData(@"NT AUTHORITY\SYSTEM", true)]
	[InlineData(@".\ivan", false)]
	[InlineData(@"NT AUTHORITY\LocalService", false)]
	public void Службы_должны_работать_от_LocalSystem(string account, bool expected)
	{
		Assert.Equal(expected, WindowsPaths.IsServiceAccount(account));
	}

	[Theory]
	[InlineData("127.0.0.1", "8080", "http://127.0.0.1:8080/")]
	[InlineData("0.0.0.0", "9000", "http://127.0.0.1:9000/")]
	[InlineData("192.168.1.10", "8080", "http://192.168.1.10:8080/")]
	public void Адрес_панели_для_этой_машины(string bind, string port, string expected)
	{
		var environment = new Dictionary<string, string> { ["PANEL_BIND"] = bind, ["PANEL_PORT"] = port };

		Assert.Equal(expected, PanelState.LocalAddress(environment));
	}

	/// <summary>Ссылка берётся из вывода <c>manage claim-token</c>, формат которого задаёт панель.</summary>
	[Fact]
	public void Ссылка_мастера_из_вывода_manage()
	{
		const string output = "Откройте мастер установки и завершите настройку:\r\n  http://127.0.0.1:8080/setup?token=abc_DEF-123\r\nСсылка одноразовая: после создания администратора токен удаляется.\r\n";

		Assert.Equal("http://127.0.0.1:8080/setup?token=abc_DEF-123", PanelState.ClaimLink(output));
		Assert.Null(PanelState.ClaimLink("Мастер установки уже завершён."));
	}

	[Fact]
	public void Doctor_Windows_на_чистой_установке_без_проблем()
	{
		Installed();

		var findings = WindowsDoctor.CheckFiles(Paths, Paths.StateDirectory);

		Assert.DoesNotContain(findings, finding => finding.Severity == Severity.Problem);
		Assert.Contains(findings, finding => finding.Text.Contains("Активная версия 2.0.0", StringComparison.Ordinal));
	}

	[Fact]
	public void Doctor_Windows_находит_бинарник_без_exe()
	{
		Installed();
		File.Delete(Path.Combine(Paths.Current, "vs-panel-agent.exe"));

		var findings = WindowsDoctor.CheckFiles(Paths, Paths.StateDirectory);

		Assert.Contains(findings, finding => finding.Severity == Severity.Problem && finding.Text.Contains("vs-panel-agent.exe", StringComparison.Ordinal));
	}

	/// <summary>Подсказки на Windows не упоминают sudo: его там нет.</summary>
	[Fact]
	public void Подсказки_doctor_на_Windows_без_sudo()
	{
		Directory.CreateDirectory(Paths.DataRoot);

		var findings = WindowsDoctor.CheckFiles(Paths, Paths.StateDirectory);

		Assert.Contains(findings, finding => finding.Severity == Severity.Problem);
		Assert.DoesNotContain(findings, finding => finding.Text.Contains("sudo", StringComparison.Ordinal));
	}

	[Fact]
	public void Doctor_Windows_напоминает_о_файлах_прежней_версии()
	{
		Installed();
		Directory.CreateDirectory(Path.Combine(Paths.DataRoot, "helper"));

		var findings = WindowsDoctor.CheckFiles(Paths, Paths.StateDirectory);

		Assert.Contains(findings, finding => finding.Severity == Severity.Warning && finding.Text.Contains("прежней версии", StringComparison.Ordinal));
	}

	private void Installed()
	{
		Directory.CreateDirectory(Paths.Current);
		File.WriteAllText(Path.Combine(Paths.Current, Artifact.VersionFile), "2.0.0\n");
		foreach (var (_, installed) in Artifact.Binaries)
		{
			File.WriteAllText(Path.Combine(Paths.Current, installed + ".exe"), "bin");
		}

		Write(Paths.PanelConfig, Templates.WindowsPanelConfig(Paths.DataRoot));
		Write(Paths.Registry, RegistrySeed.EmptyRegistry);
		Write(Paths.PanelEnv, Templates.WindowsPanelEnvironment(Paths.DataRoot));
		Write(Path.Combine(Paths.TelemetryDirectory, "vspaneltelemetry_0.3.0.zip"), "zip");
		Directory.CreateDirectory(Paths.StateDirectory);
		File.WriteAllBytes(Path.Combine(Paths.StateDirectory, "secret.key"), new byte[48]);
		Write(Path.Combine(Paths.StateDirectory, "setup.json"), "{\"completed\": true}");
	}

	private static void Write(string path, string text)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text);
	}

	private static string Repository()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VintageStoryPanel.slnx")))
		{
			directory = directory.Parent;
		}

		return directory?.FullName ?? throw new InvalidOperationException("Корень репозитория не найден");
	}
}
