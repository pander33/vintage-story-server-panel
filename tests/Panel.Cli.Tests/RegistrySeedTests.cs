using Panel.Cli;

namespace Panel.Cli.Tests;

/// <summary>
/// Реестр пишется один раз, с фиксированным порядком проверок и форматом файла.
/// </summary>
public sealed class RegistrySeedTests
{
	private const string template = "{\r\n  \"version\": 1,\r\n  \"default\": \"main\"\r\n}\r\n";

	[Fact]
	public void Существующий_реестр_не_трогается()
	{
		var decision = RegistrySeed.Decide(true, "{\"data_dir\": \"/x\"}", true, template);

		Assert.IsType<RegistryDecision.Keep>(decision.Value);
	}

	/// <summary>
	/// Плоский файл версии 1: всё, кроме ключей панели, становится сервером main, а имя по
	/// умолчанию дописывается в конец.
	/// </summary>
	[Fact]
	public void Плоский_файл_09_становится_сервером_main()
	{
		const string Flat = """
			{"unit": "vintagestory.service", "data_dir": "/srv/vintagestory/data", "staging_dir": "/var/lib/vs-panel/staging",
			 "panel_env_file": "/etc/vs-panel.env", "disk_warning_free_gb": 10, "command_transport": "fifo"}
			""";

		var decision = RegistrySeed.Decide(false, Flat, true, template);

		var migrate = Assert.IsType<RegistryDecision.MigrateFlat>(decision.Value);
		Assert.Equal(
			"""
			{
			  "version": 1,
			  "default": "main",
			  "instances": {
			    "main": {
			      "unit": "vintagestory.service",
			      "data_dir": "/srv/vintagestory/data",
			      "disk_warning_free_gb": 10,
			      "command_transport": "fifo",
			      "name": "Основной сервер"
			    }
			  }
			}

			""".Replace("\r\n", "\n", StringComparison.Ordinal),
			migrate.Json);
	}

	[Fact]
	public void Имя_из_плоского_файла_сохраняется()
	{
		var decision = RegistrySeed.Decide(false, "{\"name\": \"Мой сервер\"}", false, template);

		Assert.Contains("\"name\": \"Мой сервер\"", Assert.IsType<RegistryDecision.MigrateFlat>(decision.Value).Json, StringComparison.Ordinal);
	}

	/// <summary>Файл с ключом version уже версии 2, переносить нечего.</summary>
	[Fact]
	public void Настройки_12x_не_переносятся()
	{
		var withServer = RegistrySeed.Decide(false, "{\"version\": 2}", true, template);
		var clean = RegistrySeed.Decide(false, "{\"version\": 2}", false, template);

		Assert.Equal(template.Replace("\r\n", "\n", StringComparison.Ordinal), Assert.IsType<RegistryDecision.AdoptLegacy>(withServer.Value).Json);
		Assert.Equal(RegistrySeed.EmptyRegistry, Assert.IsType<RegistryDecision.CreateEmpty>(clean.Value).Json);
	}

	[Fact]
	public void Чистая_машина_получает_пустой_реестр()
	{
		var decision = RegistrySeed.Decide(false, null, false, template);

		Assert.Equal("{\n  \"version\": 1,\n  \"default\": \"\",\n  \"instances\": {}\n}\n", Assert.IsType<RegistryDecision.CreateEmpty>(decision.Value).Json);
	}

	/// <summary>Испорченный файл даёт отказ до записи.</summary>
	[Fact]
	public void Испорченный_плоский_файл_останавливает_установку()
	{
		Assert.False(RegistrySeed.Decide(false, "{не json", false, template).IsSuccess);
		Assert.False(RegistrySeed.Decide(false, "[1, 2]", false, template).IsSuccess);
	}
}
