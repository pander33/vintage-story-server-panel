using Panel.Fleet.Infrastructure;

namespace Panel.Fleet.Tests;

/// <summary>
/// Умолчания раскладки: для отсутствующего ключа берётся путь раскладки.
/// </summary>
public sealed class PanelConfigurationTests
{
	[Fact]
	public void Отсутствующие_пути_берутся_из_раскладки_linux()
	{
		var configuration = PanelConfiguration.Parse("""{"version": 2, "instances_file": "/etc/vs-panel/instances.json"}""").Value;

		Assert.Equal("/srv/vs-panel", configuration.InstancesRoot);
		Assert.Equal("/usr/local/share/vs-panel/telemetry", configuration.TelemetryDirectory);
		Assert.Equal("/var/lib/vs-panel/staging", configuration.StagingDirectory);
		Assert.Equal("/etc/vs-panel.env", configuration.PanelEnvFile);
		Assert.Equal("/run", configuration.RunDirectory);
	}

	[Fact]
	public void Заданные_пути_не_подменяются()
	{
		var configuration = PanelConfiguration.Parse(
			"""{"version": 2, "instances_file": "/x/i.json", "instances_root": "/data/servers", "run_dir": "/var/run"}""").Value;

		Assert.Equal("/data/servers", configuration.InstancesRoot);
		Assert.Equal("/var/run", configuration.RunDirectory);
	}

	[Fact]
	public void Плоский_файл_версии_1_тоже_получает_каталог_staging()
	{
		// Раньше у версии 1 staging оставался пустым, и загрузка мода отказывала с «каталог
		// промежуточного хранения не задан».
		var configuration = PanelConfiguration.Parse("""{"unit": "vintagestory.service", "data_dir": "/srv/vs"}""").Value;

		Assert.True(configuration.IsReadOnlyRegistry);
		Assert.Equal("/var/lib/vs-panel/staging", configuration.StagingDirectory);
	}

	[Fact]
	public void Раскладка_windows_даёт_свои_умолчания()
	{
		var configuration = PanelConfiguration.Parse("""{"version": 2, "layout": "windows", "instances_file": "C:\\x.json"}""").Value;

		Assert.Equal(@"C:\ProgramData\vs-panel\servers", configuration.InstancesRoot);
	}

	[Fact]
	public void Прежние_каталоги_серверов_читаются_и_пустые_отбрасываются()
	{
		var configuration = PanelConfiguration.Parse(
			"""{"version": 2, "instances_file": "/x/i.json", "former_instances_roots": ["/srv/vs-panel", "", 5, "/data/old"]}""").Value;

		Assert.Equal(["/srv/vs-panel", "/data/old"], configuration.FormerInstancesRoots);
	}

	[Fact]
	public void Без_прежних_каталогов_список_пуст()
	{
		var configuration = PanelConfiguration.Parse("""{"version": 2, "instances_file": "/x/i.json"}""").Value;

		Assert.Empty(configuration.FormerInstancesRoots);
	}
}
