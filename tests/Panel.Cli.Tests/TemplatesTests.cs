using System.Text.Json.Nodes;
using Panel.Cli;

namespace Panel.Cli.Tests;

/// <summary>
/// Шаблоны, которые установщик записывает на машину; их разбирают systemd, sh и демон.
/// </summary>
public sealed class TemplatesTests
{
	public static TheoryData<string> Names => new()
	{
		"vs-panel.service", "vs-panel-agent.service", "vs-panel.json", "vs-panel.env", "vs-panel-instances.json", "vs-console-wrapper",
	};

	[Theory]
	[MemberData(nameof(Names))]
	public void Шаблон_встроен_и_без_CR(string name)
	{
		var text = Templates.Read(name);

		Assert.NotEmpty(text);
		Assert.DoesNotContain('\r', text);
		Assert.EndsWith("\n", text, StringComparison.Ordinal);
	}

	/// <summary>Обновление не сбрасывает к умолчанию каталог серверов, выбранный при установке или в панели.</summary>
	[Fact]
	public void Выбранный_каталог_серверов_переживает_обновление()
	{
		var existing = """{"version": 2, "instances_file": "/etc/vs-panel/instances.json", "instances_root": "/data/vs", "former_instances_roots": ["/srv/vs-panel"]}""";

		var written = JsonNode.Parse(Templates.KeepChosenPaths(Templates.PanelConfig, existing))!;

		Assert.Equal("/data/vs", (string?) written["instances_root"]);
		Assert.Equal("/srv/vs-panel", (string?) written["former_instances_roots"]![0]);
		Assert.Equal((string?) JsonNode.Parse(Templates.PanelConfig)!["staging_dir"], (string?) written["staging_dir"]);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("не JSON")]
	[InlineData("""{"version": 2, "instances_root": ""}""")]
	[InlineData("""{"version": 2, "instances_root": "/srv/vs-panel"}""")]
	public void Без_выбора_шаблон_пишется_как_есть(string? existing)
	{
		Assert.Equal(Templates.PanelConfig, Templates.KeepChosenPaths(Templates.PanelConfig, existing));
	}

	/// <summary>Юниты запускают активную версию: при смене версии меняется ссылка, а не юниты.</summary>
	[Fact]
	public void Юниты_запускают_активную_версию()
	{
		Assert.Contains("ExecStart=/opt/vs-panel/current/vs-panel-web", Templates.PanelUnit, StringComparison.Ordinal);
		Assert.Contains("ExecStart=/opt/vs-panel/current/vs-panel-agent serve", Templates.AgentUnit(990), StringComparison.Ordinal);
	}

	/// <summary>Сокет демона открыт только одному uid, служебной учётной записи панели.</summary>
	[Fact]
	public void Юнит_демона_получает_uid_панели()
	{
		var unit = Templates.AgentUnit(997);

		Assert.Contains("--allow-uid 997", unit, StringComparison.Ordinal);
		Assert.DoesNotContain(Templates.UidPlaceholder, unit, StringComparison.Ordinal);
	}

	/// <summary>Панели sudo не нужен: юнит ужесточён, в окружении нет пути к привилегированному помощнику.</summary>
	[Fact]
	public void Панель_без_sudo_и_привилегированного_помощника()
	{
		Assert.Contains("NoNewPrivileges=true", Templates.PanelUnit, StringComparison.Ordinal);
		Assert.Contains("User=vs-panel", Templates.PanelUnit, StringComparison.Ordinal);
		Assert.DoesNotContain("PANEL_HELPER", Templates.PanelEnvironment, StringComparison.Ordinal);
	}

	[Fact]
	public void Настройки_панели_версии_2()
	{
		var config = JsonNode.Parse(Templates.PanelConfig)!.AsObject();

		Assert.Equal(2, config["version"]!.GetValue<int>());
		Assert.Equal("/etc/vs-panel/instances.json", config["instances_file"]!.GetValue<string>());
		Assert.Equal("/var/lib/vs-panel/staging", config["staging_dir"]!.GetValue<string>());
	}

	[Fact]
	public void Шаблон_сервера_в_srv_читается_как_реестр()
	{
		var registry = JsonNode.Parse(Templates.LegacyRegistry)!.AsObject();

		Assert.Equal("main", registry["default"]!.GetValue<string>());
		Assert.Equal("fifo", registry["instances"]!["main"]!["command_transport"]!.GetValue<string>());
	}

	/// <summary>Обёртка консоли на POSIX sh, без Python.</summary>
	[Fact]
	public void Обёртка_консоли_без_Python()
	{
		var wrapper = Templates.ConsoleWrapper;

		Assert.StartsWith("#!/bin/sh\n", wrapper, StringComparison.Ordinal);
		Assert.DoesNotContain(
			wrapper.Split('\n').Where(line => !line.TrimStart().StartsWith('#')),
			line => line.Contains("python", StringComparison.OrdinalIgnoreCase));
		Assert.Contains("mkfifo -m 600", wrapper, StringComparison.Ordinal);
		Assert.Contains("exec 0<>\"$fifo\"", wrapper, StringComparison.Ordinal);
	}
}
