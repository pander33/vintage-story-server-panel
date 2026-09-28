using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Panel.Cli;

internal enum Severity
{
	Ok,
	Warning,
	Problem,
}

internal sealed record Finding(Severity Severity, string Text)
{
	public string Mark => Severity switch
	{
		Severity.Ok => "✓",
		Severity.Warning => "!",
		_ => "✗",
	};
}

/// <summary>
/// <c>vs-panel doctor</c>: проверка установки без изменений — что установлено, что запущено,
/// что осталось от прежней версии панели. Ничего не исправляет; что делать, написано в тексте
/// находки.
/// </summary>
/// <remarks>
/// Файловые проверки отделены от проверок служб: первые не зависят от платформы и
/// тестируются на любом каталоге, вторые обращаются к systemd и работают только на сервере.
/// </remarks>
/// </remarks>
internal static class Doctor
{
	public static IReadOnlyList<Finding> CheckFiles(InstallPaths paths, string stateDirectory)
	{
		ArgumentNullException.ThrowIfNull(paths);
		var findings = new List<Finding>();

		var version = FirstLine(Path.Combine(paths.Current, Artifact.VersionFile));
		if (version is null)
		{
			findings.Add(new(Severity.Problem, $"Panel is not installed: missing {paths.Current}/{Artifact.VersionFile}. Install it: sudo sh install.sh"));
		}
		else
		{
			var missing = Artifact.Binaries.Where(binary => !File.Exists(Path.Combine(paths.Current, binary.Installed))).Select(binary => binary.Installed).ToList();
			findings.Add(missing.Count == 0
				? new(Severity.Ok, $"Active version {version}")
				: new(Severity.Problem, $"Version {version} is missing {string.Join(", ", missing)}: reinstall it (sudo vs-panel update --from <build directory>)"));
		}

		findings.Add(CheckPanelConfig(paths.PanelConfig));
		findings.Add(CheckRegistry(paths.Registry));
		findings.Add(CheckLinuxText(paths.PanelEnv, "Environment file"));
		findings.Add(CheckLinuxText(paths.PanelUnit, "Panel unit"));
		findings.Add(CheckLinuxText(paths.AgentUnit, "Agent unit"));
		findings.Add(CheckWrapper(paths.ConsoleWrapper));
		findings.AddRange(CheckState(stateDirectory));

		findings.Add(Directory.Exists(paths.TelemetryDirectory) && Directory.GetFiles(paths.TelemetryDirectory, "vspaneltelemetry_*.zip").Length > 0
			? new(Severity.Ok, $"Telemetry mod for new servers: {paths.TelemetryDirectory}")
			: new(Severity.Warning, $"No telemetry mod in {paths.TelemetryDirectory}: readiness checks for new servers are limited"));

		foreach (var path in new[] { paths.LegacySudoers, paths.LegacyHelper, paths.LegacyClaim })
		{
			if (File.Exists(path))
			{
				findings.Add(new(Severity.Problem, $"Legacy file remains at {path}: the new panel does not need sudo. Reinstall or remove the file"));
			}
		}

		var leftovers = paths.LegacyProgram.Where(path => File.Exists(path) || Directory.Exists(path)).ToList();
		if (leftovers.Count > 0)
		{
			findings.Add(new(Severity.Warning, $"Legacy panel files remain: {string.Join(", ", leftovers)}. The new panel does not use them; remove them manually"));
		}

		return findings;
	}

	[SupportedOSPlatform("linux")]
	public static async Task<IReadOnlyList<Finding>> CheckServicesAsync(LinuxHost host)
	{
		ArgumentNullException.ThrowIfNull(host);
		var findings = new List<Finding>();
		foreach (var unit in new[] { InstallPaths.AgentUnitName, InstallPaths.PanelUnitName })
		{
			var active = await host.UnitPropertyAsync(unit, "ActiveState").ConfigureAwait(false);
			var enabled = (await host.SystemctlAsync("is-enabled", unit).ConfigureAwait(false)).Output.Trim();
			findings.Add(active == "active" && enabled == "enabled"
				? new(Severity.Ok, $"{unit}: running, enabled")
				: new(Severity.Problem, $"{unit}: {(active.Length > 0 ? active : "unknown")}, {(enabled.Length > 0 ? enabled : "not enabled")}. Log: journalctl -u {unit} -n 100 --no-pager"));
		}

		findings.Add(File.Exists(host.Paths.AgentSocket)
			? new(Severity.Ok, $"Agent socket {host.Paths.AgentSocket}")
			: new(Severity.Problem, $"Missing agent socket {host.Paths.AgentSocket}: the panel cannot execute commands"));

		var owner = await Shell.RunAsync("stat", ["-c", "%U %a", host.StateDirectory()]).ConfigureAwait(false);
		findings.Add(owner.Succeeded && owner.Output.Trim() == $"{InstallPaths.PanelUser} 700"
			? new(Severity.Ok, $"State directory belongs to {InstallPaths.PanelUser}, mode 700")
			: new(Severity.Problem, $"State directory {host.StateDirectory()}: {owner.Output.Trim()} instead of {InstallPaths.PanelUser} 700. Fix with sudo vs-panel repair"));

		findings.Add(File.Exists("/usr/bin/setpriv")
			? new(Severity.Ok, "setpriv is present: the game does not run as root")
			: new(Severity.Problem, "Missing /usr/bin/setpriv (util-linux): new servers cannot be created"));

		var dotnet = await Shell.RunAsync("dotnet", ["--list-runtimes"]).ConfigureAwait(false);
		findings.Add(dotnet.Succeeded && dotnet.Output.Contains("Microsoft.NETCore.App 10.", StringComparison.Ordinal)
			? new(Severity.Ok, ".NET 10 runtime for the game is installed")
			: new(Severity.Warning, ".NET 10 runtime for the game was not found: Vintage Story 1.22 servers will not start"));
		return findings;
	}

	public static int Print(IEnumerable<Finding> findings, TextWriter output)
	{
		ArgumentNullException.ThrowIfNull(findings);
		ArgumentNullException.ThrowIfNull(output);
		var worst = Severity.Ok;
		foreach (var finding in findings)
		{
			output.WriteLine($"{finding.Mark} {finding.Text}");
			worst = finding.Severity > worst ? finding.Severity : worst;
		}

		return worst == Severity.Problem ? 1 : 0;
	}

	internal static Finding CheckPanelConfig(string path)
	{
		if (Json(path) is not { } config)
		{
			return new(Severity.Problem, $"Cannot read {path}: reinstall the panel");
		}

		var version = config["version"] is JsonValue value && value.TryGetValue<int>(out var number) ? number : 1;
		return version >= 2
			? new(Severity.Ok, $"Panel settings {path}: version {version}")
			: new(Severity.Problem, $"{path} is version 1: the registry is read-only and servers cannot be created. Reinstall the panel");
	}

	internal static Finding CheckRegistry(string path)
	{
		if (Json(path) is not { } registry)
		{
			return new(Severity.Problem, $"Cannot read server registry {path}");
		}

		var count = (registry["instances"] as JsonObject)?.Count ?? 0;
		return new(Severity.Ok, count == 0 ? $"Registry {path}: no servers yet" : $"Registry {path}: {count} servers");
	}

	/// <summary>Файлы, которые разбирает Linux: CR в них ломает systemd или sh.</summary>
	private static Finding CheckLinuxText(string path, string title)
	{
		if (!File.Exists(path))
		{
			return new(Severity.Problem, $"{title}: missing {path}. Fix with sudo vs-panel repair");
		}

		return File.ReadAllText(path).Contains('\r', StringComparison.Ordinal)
			? new(Severity.Problem, $"{title} {path} contains CRLF: systemd and sh will treat CR as part of the value. Fix with sudo vs-panel repair")
			: new(Severity.Ok, $"{title}: {path}");
	}

	private static Finding CheckWrapper(string path)
	{
		if (!File.Exists(path))
		{
			return new(Severity.Problem, $"Missing console wrapper {path}: servers using fifo transport will not start. Fix with sudo vs-panel repair");
		}

		var text = File.ReadAllText(path);
		if (text.Contains('\r', StringComparison.Ordinal))
		{
			return new(Severity.Problem, $"Console wrapper {path} contains CRLF. Fix with sudo vs-panel repair");
		}

		return text.StartsWith("#!/bin/sh", StringComparison.Ordinal)
			? new(Severity.Ok, $"Console wrapper {path}")
			: new(Severity.Warning, $"Console wrapper {path} is from the previous Python version. Replace with sudo vs-panel repair");
	}

	internal static IEnumerable<Finding> CheckState(string state, string command = "sudo vs-panel")
	{
		if (!Directory.Exists(state))
		{
			yield return new(Severity.Problem, $"Missing state directory {state}. Fix with {command} repair");
			yield break;
		}

		var secret = new FileInfo(Path.Combine(state, "secret.key"));
		yield return secret.Exists && secret.Length >= 32
			? new(Severity.Ok, "Session key is present")
			: new(Severity.Problem, $"Missing session key {secret.FullName} (or shorter than 32 bytes): the panel will not start. Fix with {command} repair");

		yield return Json(Path.Combine(state, "setup.json"))?["completed"] is JsonValue flag && flag.TryGetValue<bool>(out var done) && done
			? new(Severity.Ok, "Setup wizard is complete")
			: new(Severity.Warning, $"Setup wizard is incomplete: get its link with {command} access");

		if (MaintenanceProbe.Describe(state) is { } busy)
		{
			yield return new(Severity.Warning, $"The panel is running {busy}: it cannot be updated or rolled back now");
		}
	}

	private static JsonObject? Json(string path)
	{
		try
		{
			return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return null;
		}
	}

	private static string? FirstLine(string path)
	{
		try
		{
			return File.ReadLines(path).FirstOrDefault()?.Trim();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}
}
