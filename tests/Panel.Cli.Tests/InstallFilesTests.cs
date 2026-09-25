using System.Text.RegularExpressions;
using Panel.Cli;

namespace Panel.Cli.Tests;

/// <summary>
/// Каталог сборки, признаки идущей операции и настройки установки: всё, что установщик
/// решает до первого изменения машины. На временных каталогах.
/// </summary>
public sealed class InstallFilesTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "vs-cli-" + Guid.NewGuid().ToString("n"));

	public InstallFilesTests() => Directory.CreateDirectory(directory);

	public void Dispose()
	{
		if (Directory.Exists(directory))
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	private void Build(string version = "0.2.0", bool installedNames = false)
	{
		foreach (var (published, installed) in Artifact.Binaries)
		{
			File.WriteAllText(Path.Combine(directory, installedNames ? installed : published), "bin");
		}

		File.WriteAllText(Path.Combine(directory, Artifact.VersionFile), version + "\n");
		File.WriteAllText(Path.Combine(directory, Artifact.RidFile), "linux-x64\n");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Сборка_открывается_под_любыми_именами_бинарников(bool installedNames)
	{
		Build(installedNames: installedNames);

		var artifact = Artifact.Open(directory);

		Assert.True(artifact.IsSuccess);
		Assert.Equal("0.2.0", artifact.Value.Version);
		Assert.Equal("linux-x64", artifact.Value.Rid);
		Assert.All(Artifact.Binaries, binary => Assert.True(File.Exists(artifact.Value.Source(binary))));
	}

	[Fact]
	public void Без_бинарника_это_не_сборка()
	{
		Build();
		File.Delete(Path.Combine(directory, "Panel.Agent"));

		var artifact = Artifact.Open(directory);

		Assert.False(artifact.IsSuccess);
		Assert.Contains("Panel.Agent", artifact.Failure!.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("")]
	[InlineData("../../etc")]
	public void Без_годной_версии_это_не_сборка(string version)
	{
		Build(version);

		Assert.False(Artifact.Open(directory).IsSuccess);
	}

	/// <summary>0.10.0 новее 0.9.0, как в <c>sort -V</c>, а не при сравнении строк.</summary>
	[Fact]
	public void Берётся_самый_новый_мод_телеметрии()
	{
		Build();
		var telemetry = Path.Combine(directory, "telemetry");
		Directory.CreateDirectory(telemetry);
		foreach (var name in new[] { "vspaneltelemetry_0.9.0.zip", "vspaneltelemetry_0.10.0.zip", "vspaneltelemetry_0.2.0.zip" })
		{
			File.WriteAllText(Path.Combine(telemetry, name), "zip");
		}

		Assert.Equal("vspaneltelemetry_0.10.0.zip", Path.GetFileName(Artifact.Open(directory).Value.NewestTelemetry));
	}

	[Fact]
	public void Пакет_с_номером_версии_выбирается_самый_новый()
	{
		string[] names = ["libicu72", "libicu74", "libicu-dev", "libssl3", "libssl3t64", "libssl-dev"];

		Assert.Equal("libicu74", PackageNames.Newest(names, PackageNames.Icu));
		Assert.Equal("libssl3t64", PackageNames.Newest(names, PackageNames.Ssl));
		Assert.Null(PackageNames.Newest(names, new Regex(@"\Alibfoo\z")));
	}

	[Fact]
	public void Свободная_панель_не_занята()
	{
		File.WriteAllText(Path.Combine(directory, "updates.json"), "{\"current\": null, \"history\": []}");

		Assert.Null(MaintenanceProbe.Describe(directory));
	}

	/// <summary>Аренда обслуживания означает любую долгую операцию панели 2.0.</summary>
	[Fact]
	public void Аренда_обслуживания_занимает_панель()
	{
		File.WriteAllText(Path.Combine(directory, "maintenance-lease.json"), "{\"purpose\": \"game-update\", \"instance\": \"main\"}");

		Assert.Equal("обслуживание (game-update, сервер main)", MaintenanceProbe.Describe(directory));
	}

	/// <summary>Операция второго сервера тоже учитывается.</summary>
	[Fact]
	public void Обновление_любого_сервера_занимает_панель()
	{
		var creative = Path.Combine(directory, "instances", "creative");
		Directory.CreateDirectory(creative);
		File.WriteAllText(Path.Combine(creative, "updates.json"), "{\"current\": {\"title\": \"Обновление игры\", \"phase\": \"backup\"}}");

		Assert.Equal("Обновление игры (этап: backup)", MaintenanceProbe.Describe(directory));
	}

	[Fact]
	public void Создание_сервера_занимает_панель()
	{
		File.WriteAllText(Path.Combine(directory, "provision.json"), "{\"current\": {\"status\": \"running\"}}");

		Assert.Equal("операция обновления (этап: running)", MaintenanceProbe.Describe(directory));
	}

	[Fact]
	public void Настройки_установки_из_окружения_12x()
	{
		var environment = new Dictionary<string, string>
		{
			["VS_PANEL_CREATE_SERVER"] = "0",
			["VS_PANEL_GAME_VERSION"] = "1.21.6",
			["VS_PANEL_GAME_PORT"] = "42500",
			["VS_PANEL_INSTALL_WAIT_SECONDS"] = "300",
			["VS_PANEL_SKIP_DOTNET"] = "1",
		};

		var options = InstallOptions.Parse(["--from", "/tmp/build"], environment, InstallPaths.System);

		Assert.True(options.IsSuccess);
		Assert.False(options.Value.CreateServer);
		Assert.Equal("1.21.6", options.Value.GameVersion);
		Assert.Equal(42500, options.Value.GamePort);
		Assert.Equal(300, options.Value.WaitSeconds);
		Assert.True(options.Value.SkipDotnet);
		Assert.Equal("/tmp/build", options.Value.From);
		Assert.Equal(InstallPaths.System.LegacyModsDirectory, options.Value.TelemetryModDirectory);
	}

	[Fact]
	public void Умолчания_установки()
	{
		var options = InstallOptions.Parse([], new Dictionary<string, string>(), InstallPaths.System).Value;

		Assert.True(options.CreateServer);
		Assert.Equal(120, options.WaitSeconds);
		Assert.Equal("10.0", options.DotnetChannel);
		Assert.Equal(0, options.GamePort);
	}

	[Theory]
	[InlineData("VS_PANEL_INSTALL_WAIT_SECONDS", "5")]
	[InlineData("VS_PANEL_INSTALL_WAIT_SECONDS", "долго")]
	[InlineData("VS_PANEL_GAME_PORT", "80")]
	public void Неверные_настройки_установки_отвергаются(string key, string value)
	{
		Assert.False(InstallOptions.Parse([], new Dictionary<string, string> { [key] = value }, InstallPaths.System).IsSuccess);
	}

	[Fact]
	public void Каталог_серверов_из_параметра_важнее_переменной()
	{
		var environment = new Dictionary<string, string> { ["VS_PANEL_SERVERS_DIR"] = "/data/env" };

		Assert.Equal("/data/env", InstallOptions.Parse([], environment, InstallPaths.System).Value.ServersDirectory);
		Assert.Equal("/data/arg", InstallOptions.Parse(["--servers-dir", "/data/arg"], environment, InstallPaths.System).Value.ServersDirectory);
		Assert.Equal(@"D:\vs", InstallOptions.ParseWindows(["--servers-dir", @"D:\vs"], new Dictionary<string, string>()).Value.ServersDirectory);
	}

	[Fact]
	public void Параметр_каталога_серверов_без_значения_отвергается()
	{
		Assert.False(InstallOptions.Parse(["--servers-dir"], new Dictionary<string, string>(), InstallPaths.System).IsSuccess);
		Assert.False(InstallOptions.Parse(["--servers-dir", " "], new Dictionary<string, string>(), InstallPaths.System).IsSuccess);
	}

	[Fact]
	public void Неизвестный_параметр_установки_отвергается()
	{
		Assert.False(InstallOptions.Parse(["--force"], new Dictionary<string, string>(), InstallPaths.System).IsSuccess);
	}
}
