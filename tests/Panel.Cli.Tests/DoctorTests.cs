using Panel.Cli;

namespace Panel.Cli.Tests;

/// <summary>
/// Файловая часть <c>vs-panel doctor</c> на поддельном корне. Активная версия здесь каталог, а
/// не ссылка: проверка читает <c>current/VERSION</c>, и разницы нет.
/// </summary>
public sealed class DoctorTests : IDisposable
{
	private readonly string root = Path.Combine(Path.GetTempPath(), "vs-doctor-" + Guid.NewGuid().ToString("n"));

	private InstallPaths Paths => new(root);

	private string State => Paths.StateDirectory;

	public void Dispose()
	{
		if (Directory.Exists(root))
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private void Installed()
	{
		Directory.CreateDirectory(Paths.Current);
		File.WriteAllText(Path.Combine(Paths.Current, Artifact.VersionFile), "0.2.0\n");
		foreach (var (_, installed) in Artifact.Binaries)
		{
			File.WriteAllText(Path.Combine(Paths.Current, installed), "bin");
		}

		Write(Paths.PanelConfig, Templates.PanelConfig);
		Write(Paths.Registry, RegistrySeed.EmptyRegistry);
		Write(Paths.PanelEnv, Templates.PanelEnvironment);
		Write(Paths.PanelUnit, Templates.PanelUnit);
		Write(Paths.AgentUnit, Templates.AgentUnit(990));
		Write(Paths.ConsoleWrapper, Templates.ConsoleWrapper);
		Write(Path.Combine(Paths.TelemetryDirectory, "vspaneltelemetry_0.3.0.zip"), "zip");
		Directory.CreateDirectory(State);
		File.WriteAllBytes(Path.Combine(State, "secret.key"), new byte[48]);
		Write(Path.Combine(State, "setup.json"), "{\"completed\": true}");
	}

	private static void Write(string path, string text)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text);
	}

	[Fact]
	public void Чистая_установка_без_находок()
	{
		Installed();

		var findings = Doctor.CheckFiles(Paths, State);

		Assert.All(findings, finding => Assert.Equal(Severity.Ok, finding.Severity));
		Assert.Equal(0, Doctor.Print(findings, TextWriter.Null));
	}

	[Fact]
	public void Без_установки_проблема()
	{
		var findings = Doctor.CheckFiles(Paths, State);

		Assert.Contains(findings, finding => finding.Severity == Severity.Problem && finding.Text.Contains("not installed", StringComparison.Ordinal));
		Assert.Equal(1, Doctor.Print(findings, TextWriter.Null));
	}

	/// <summary>CR в файле окружения systemd считает частью значения.</summary>
	[Fact]
	public void CRLF_в_файле_окружения_проблема()
	{
		Installed();
		File.WriteAllText(Paths.PanelEnv, "PANEL_PORT=8080\r\n");

		Assert.Contains(Doctor.CheckFiles(Paths, State), finding => finding.Severity == Severity.Problem && finding.Text.Contains("CRLF", StringComparison.Ordinal));
	}

	/// <summary>sudo-правило прежней версии панели открывало бы её учётной записи доступ к root.</summary>
	[Fact]
	public void Оставшееся_правило_sudo_проблема()
	{
		Installed();
		Write(Paths.LegacySudoers, "vs-panel ALL=(root) NOPASSWD: /usr/local/libexec/vs-panel-helper");

		Assert.Contains(Doctor.CheckFiles(Paths, State), finding => finding.Severity == Severity.Problem && finding.Text.Contains("sudo", StringComparison.Ordinal));
	}

	[Fact]
	public void Дерево_прежней_версии_на_Python_предупреждение()
	{
		Installed();
		Directory.CreateDirectory(Path.Combine(Paths.ProgramRoot, ".venv"));
		Write(Paths.ConsoleWrapper, "#!/usr/bin/env python3\nprint()\n");

		var findings = Doctor.CheckFiles(Paths, State);

		Assert.Contains(findings, finding => finding.Severity == Severity.Warning && finding.Text.Contains(".venv", StringComparison.Ordinal));
		Assert.Contains(findings, finding => finding.Severity == Severity.Warning && finding.Text.Contains("Python", StringComparison.Ordinal));
		Assert.Equal(0, Doctor.Print(findings, TextWriter.Null));
	}

	[Fact]
	public void Короткий_ключ_сессий_проблема()
	{
		Installed();
		File.WriteAllBytes(Path.Combine(State, "secret.key"), new byte[16]);

		Assert.Contains(Doctor.CheckFiles(Paths, State), finding => finding.Severity == Severity.Problem && finding.Text.Contains("session key", StringComparison.Ordinal));
	}

	[Fact]
	public void Настройки_версии_1_проблема()
	{
		Installed();
		File.WriteAllText(Paths.PanelConfig, "{\"data_dir\": \"/srv/vintagestory/data\"}");

		Assert.Contains(Doctor.CheckFiles(Paths, State), finding => finding.Severity == Severity.Problem && finding.Text.Contains("version 1", StringComparison.Ordinal));
	}

	[Fact]
	public void Незавершённый_мастер_и_идущая_операция_предупреждения()
	{
		Installed();
		File.WriteAllText(Path.Combine(State, "setup.json"), "{\"completed\": false}");
		File.WriteAllText(Path.Combine(State, "maintenance-lease.json"), "{\"purpose\": \"provision\", \"instance\": \"main\"}");

		var findings = Doctor.CheckFiles(Paths, State);

		Assert.Contains(findings, finding => finding.Severity == Severity.Warning && finding.Text.Contains("vs-panel access", StringComparison.Ordinal));
		Assert.Contains(findings, finding => finding.Severity == Severity.Warning && finding.Text.Contains("provision", StringComparison.Ordinal));
	}
}
