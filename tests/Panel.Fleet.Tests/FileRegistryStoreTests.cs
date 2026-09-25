using Panel.Fleet.Domain;
using Panel.Fleet.Infrastructure;
using Panel.SharedKernel;

namespace Panel.Fleet.Tests;

public sealed class FileRegistryStoreTests : IDisposable
{
	private readonly string root = Path.Combine(
		Path.GetTempPath(), "vs-panel-net-tests", Guid.NewGuid().ToString("N"));

	public FileRegistryStoreTests() => Directory.CreateDirectory(root);

	public void Dispose()
	{
		try
		{
			Directory.Delete(root, recursive: true);
		}
		catch (IOException)
		{
			// Ошибка уборки временного каталога не влияет на результат теста.
		}
	}

	[Fact]
	public async Task Реестр_версии_2_читается()
	{
		var registry = Path.Combine(root, "instances.json");
		File.WriteAllText(registry, """
        {
          "version": 1,
          "default": "second",
          "instances": {
            "second": {"name": "Второй", "unit": "vs-second.service",
                       "telemetry_file": "/srv/second/VsPanel/telemetry.json"},
            "first":  {"name": "", "unit": "vs-first.service"}
          }
        }
        """);

		var snapshot = await Load(PanelConfig(registry));

		var available = Assert.IsType<RegistrySnapshot.Available>(snapshot);
		Assert.Equal(2, available.Servers.Count);
		Assert.Equal("second", available.EffectiveDefault!.Id.Value);
		Assert.Equal("/srv/second/VsPanel/telemetry.json", available.EffectiveDefault.TelemetryFile);

		// Пустое имя означает, что сервер называется по идентификатору.
		Assert.Equal("first", available.Find(InstanceId.Parse("first"))!.Name);
	}

	[Fact]
	public async Task Плоский_файл_версии_1_становится_единственным_сервером_main()
	{
		var config = Path.Combine(root, "vs-panel.json");
		File.WriteAllText(config, """
        {"version": 1, "unit": "vintagestory.service",
         "telemetry_file": "/srv/vintagestory/data/VsPanel/telemetry.json"}
        """);

		var snapshot = await Load(config);

		var available = Assert.IsType<RegistrySnapshot.Available>(snapshot);
		var server = Assert.Single(available.Servers);
		Assert.Equal("main", server.Id.Value);
		Assert.Equal("vintagestory.service", server.Unit);
	}

	/// <summary>
	/// Недоступный реестр не считается пустым.
	/// </summary>
	[Fact]
	public async Task Повреждённый_реестр_даёт_Unavailable_а_не_пустой_список()
	{
		var registry = Path.Combine(root, "instances.json");
		File.WriteAllText(registry, "{ это не json");

		var snapshot = await Load(PanelConfig(registry));

		Assert.IsType<RegistrySnapshot.Unavailable>(snapshot);
	}

	[Fact]
	public async Task Отсутствующий_файл_реестра_означает_пустой_реестр()
	{
		// Так выглядит машина до мастера установки: серверов нет, и это допустимо.
		var snapshot = await Load(PanelConfig(Path.Combine(root, "нет-такого.json")));

		var available = Assert.IsType<RegistrySnapshot.Available>(snapshot);
		Assert.Empty(available.Servers);
		Assert.Null(available.EffectiveDefault);
	}

	[Fact]
	public async Task Объявленный_но_отсутствующий_сервер_по_умолчанию_не_подменяет_собой_реальный()
	{
		var registry = Path.Combine(root, "instances.json");
		File.WriteAllText(registry, """
        {"version": 1, "default": "удалённый",
         "instances": {"alpha": {"unit": "vs-alpha.service"}}}
        """);

		var snapshot = await Load(PanelConfig(registry));

		var available = Assert.IsType<RegistrySnapshot.Available>(snapshot);
		Assert.Equal("alpha", available.EffectiveDefault!.Id.Value);
	}

	[Fact]
	public async Task Запись_с_недопустимым_идентификатором_пропускается_а_остальные_остаются()
	{
		var registry = Path.Combine(root, "instances.json");
		File.WriteAllText(registry, """
        {"version": 1,
         "instances": {"ПЛОХОЙ": {"unit": "x.service"}, "good": {"unit": "vs-good.service"}}}
        """);

		var snapshot = await Load(PanelConfig(registry));

		var available = Assert.IsType<RegistrySnapshot.Available>(snapshot);
		Assert.Equal("good", Assert.Single(available.Servers).Id.Value);
	}

	private string PanelConfig(string registryPath)
	{
		var config = Path.Combine(root, "vs-panel.json");
		File.WriteAllText(config, $$"""
        {"version": 2, "layout": "linux", "instances_file": {{System.Text.Json.JsonSerializer.Serialize(registryPath)}}}
        """);
		return config;
	}

	private static Task<RegistrySnapshot> Load(string panelConfig) =>
		new FileRegistryStore(panelConfig).LoadAsync(CancellationToken.None);
}
