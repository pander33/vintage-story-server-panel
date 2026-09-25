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
			findings.Add(new(Severity.Problem, $"Панель не установлена: нет {paths.Current}/{Artifact.VersionFile}. Установите: sudo sh install.sh"));
		}
		else
		{
			var missing = Artifact.Binaries.Where(binary => !File.Exists(Path.Combine(paths.Current, binary.Installed))).Select(binary => binary.Installed).ToList();
			findings.Add(missing.Count == 0
				? new(Severity.Ok, $"Активная версия {version}")
				: new(Severity.Problem, $"В версии {version} нет {string.Join(", ", missing)}: переустановите её (sudo vs-panel update --from <каталог сборки>)"));
		}

		findings.Add(CheckPanelConfig(paths.PanelConfig));
		findings.Add(CheckRegistry(paths.Registry));
		findings.Add(CheckLinuxText(paths.PanelEnv, "Файл окружения"));
		findings.Add(CheckLinuxText(paths.PanelUnit, "Юнит панели"));
		findings.Add(CheckLinuxText(paths.AgentUnit, "Юнит демона"));
		findings.Add(CheckWrapper(paths.ConsoleWrapper));
		findings.AddRange(CheckState(stateDirectory));

		findings.Add(Directory.Exists(paths.TelemetryDirectory) && Directory.GetFiles(paths.TelemetryDirectory, "vspaneltelemetry_*.zip").Length > 0
			? new(Severity.Ok, $"Мод телеметрии для новых серверов: {paths.TelemetryDirectory}")
			: new(Severity.Warning, $"Нет мода телеметрии в {paths.TelemetryDirectory}: готовность новых серверов проверяется ограниченно"));

		foreach (var path in new[] { paths.LegacySudoers, paths.LegacyHelper, paths.LegacyClaim })
		{
			if (File.Exists(path))
			{
				findings.Add(new(Severity.Problem, $"Остался файл прежней версии {path}: панели 2.0 sudo не нужен. Повторите установку или удалите файл"));
			}
		}

		var leftovers = paths.LegacyProgram.Where(path => File.Exists(path) || Directory.Exists(path)).ToList();
		if (leftovers.Count > 0)
		{
			findings.Add(new(Severity.Warning, $"Остались файлы прежней версии панели: {string.Join(", ", leftovers)}. Панель 2.0 их не использует; удалить можно вручную"));
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
				? new(Severity.Ok, $"{unit}: работает, включён")
				: new(Severity.Problem, $"{unit}: {(active.Length > 0 ? active : "неизвестно")}, {(enabled.Length > 0 ? enabled : "не включён")}. Журнал: journalctl -u {unit} -n 100 --no-pager"));
		}

		findings.Add(File.Exists(host.Paths.AgentSocket)
			? new(Severity.Ok, $"Сокет демона {host.Paths.AgentSocket}")
			: new(Severity.Problem, $"Нет сокета демона {host.Paths.AgentSocket}: панель не может выполнять команды"));

		var owner = await Shell.RunAsync("stat", ["-c", "%U %a", host.StateDirectory()]).ConfigureAwait(false);
		findings.Add(owner.Succeeded && owner.Output.Trim() == $"{InstallPaths.PanelUser} 700"
			? new(Severity.Ok, $"Каталог состояния принадлежит {InstallPaths.PanelUser}, режим 700")
			: new(Severity.Problem, $"Каталог состояния {host.StateDirectory()}: {owner.Output.Trim()} вместо «{InstallPaths.PanelUser} 700». Исправит sudo vs-panel repair"));

		findings.Add(File.Exists("/usr/bin/setpriv")
			? new(Severity.Ok, "setpriv есть: игра запускается не от root")
			: new(Severity.Problem, "Нет /usr/bin/setpriv (пакет util-linux): новые серверы не создаются"));

		var dotnet = await Shell.RunAsync("dotnet", ["--list-runtimes"]).ConfigureAwait(false);
		findings.Add(dotnet.Succeeded && dotnet.Output.Contains("Microsoft.NETCore.App 10.", StringComparison.Ordinal)
			? new(Severity.Ok, "Среда .NET 10 для игры установлена")
			: new(Severity.Warning, "Не найдена среда .NET 10 для игры: серверы Vintage Story 1.22 не запустятся"));
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
			return new(Severity.Problem, $"Не читается {path}: повторите установку");
		}

		var version = config["version"] is JsonValue value && value.TryGetValue<int>(out var number) ? number : 1;
		return version >= 2
			? new(Severity.Ok, $"Настройки панели {path}: версия {version}")
			: new(Severity.Problem, $"{path} версии 1: реестр только на чтение, серверы не создаются. Повторите установку");
	}

	internal static Finding CheckRegistry(string path)
	{
		if (Json(path) is not { } registry)
		{
			return new(Severity.Problem, $"Не читается реестр серверов {path}");
		}

		var count = (registry["instances"] as JsonObject)?.Count ?? 0;
		return new(Severity.Ok, count == 0 ? $"Реестр {path}: серверов пока нет" : $"Реестр {path}: серверов — {count}");
	}

	/// <summary>Файлы, которые разбирает Linux: CR в них ломает systemd или sh.</summary>
	private static Finding CheckLinuxText(string path, string title)
	{
		if (!File.Exists(path))
		{
			return new(Severity.Problem, $"{title}: нет {path}. Исправит sudo vs-panel repair");
		}

		return File.ReadAllText(path).Contains('\r', StringComparison.Ordinal)
			? new(Severity.Problem, $"{title} {path} содержит CRLF: systemd и sh прочитают CR как часть значения. Исправит sudo vs-panel repair")
			: new(Severity.Ok, $"{title}: {path}");
	}

	private static Finding CheckWrapper(string path)
	{
		if (!File.Exists(path))
		{
			return new(Severity.Problem, $"Нет обёртки консоли {path}: серверы с транспортом fifo не запустятся. Исправит sudo vs-panel repair");
		}

		var text = File.ReadAllText(path);
		if (text.Contains('\r', StringComparison.Ordinal))
		{
			return new(Severity.Problem, $"Обёртка консоли {path} содержит CRLF. Исправит sudo vs-panel repair");
		}

		return text.StartsWith("#!/bin/sh", StringComparison.Ordinal)
			? new(Severity.Ok, $"Обёртка консоли {path}")
			: new(Severity.Warning, $"Обёртка консоли {path} — от прежней версии (Python). Заменит sudo vs-panel repair");
	}

	internal static IEnumerable<Finding> CheckState(string state, string command = "sudo vs-panel")
	{
		if (!Directory.Exists(state))
		{
			yield return new(Severity.Problem, $"Нет каталога состояния {state}. Исправит {command} repair");
			yield break;
		}

		var secret = new FileInfo(Path.Combine(state, "secret.key"));
		yield return secret.Exists && secret.Length >= 32
			? new(Severity.Ok, "Ключ сессий на месте")
			: new(Severity.Problem, $"Нет ключа сессий {secret.FullName} (или он короче 32 байт): панель не запустится. Исправит {command} repair");

		yield return Json(Path.Combine(state, "setup.json"))?["completed"] is JsonValue flag && flag.TryGetValue<bool>(out var done) && done
			? new(Severity.Ok, "Мастер установки завершён")
			: new(Severity.Warning, $"Мастер установки не завершён: ссылка на него — {command} access");

		if (MaintenanceProbe.Describe(state) is { } busy)
		{
			yield return new(Severity.Warning, $"Панель выполняет {busy}: обновлять и откатывать её сейчас нельзя");
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
