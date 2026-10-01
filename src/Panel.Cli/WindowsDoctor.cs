using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;

namespace Panel.Cli;

/// <summary>
/// <c>vs-panel doctor</c> на Windows: что установлено, что запущено, что осталось от прежней
/// версии панели. Ничего не меняет; что делать, написано в тексте находки.
/// </summary>
/// <remarks>
/// Как и в <see cref="Doctor"/>, файловые проверки отделены от проверок служб: первые
/// тестируются на любом каталоге, вторые обращаются к диспетчеру служб.
/// </remarks>
internal static class WindowsDoctor
{
	private const string command = "vs-panel";

	public static IReadOnlyList<Finding> CheckFiles(WindowsPaths paths, string stateDirectory)
	{
		ArgumentNullException.ThrowIfNull(paths);
		var findings = new List<Finding>();

		var version = FirstLine(Path.Combine(paths.Current, Artifact.VersionFile));
		if (version is null)
		{
			findings.Add(new(Severity.Problem, $"Panel is not installed: missing {Path.Combine(paths.Current, Artifact.VersionFile)}. Install with install.cmd from the build directory"));
		}
		else
		{
			var missing = Artifact.Binaries
				.Select(binary => binary.Installed + Artifact.Suffix(windows: true))
				.Where(name => !File.Exists(Path.Combine(paths.Current, name)))
				.ToList();
			findings.Add(missing.Count == 0
				? new(Severity.Ok, $"Active version {version}")
				: new(Severity.Problem, $"Version {version} is missing {string.Join(", ", missing)}: reinstall with install.cmd from the build directory"));
		}

		findings.Add(Doctor.CheckPanelConfig(paths.PanelConfig));
		findings.Add(Doctor.CheckRegistry(paths.Registry));
		findings.Add(!File.Exists(paths.PanelEnv)
			? new(Severity.Problem, $"Missing environment file {paths.PanelEnv}. Fix with {command} repair")
			: !Path.IsPathFullyQualified(stateDirectory)
				? new(Severity.Problem, $"PANEL_STATE_DIR in {paths.PanelEnv} is not an absolute path: {stateDirectory}. Fix with {command} repair")
				: new(Severity.Ok, $"Environment file: {paths.PanelEnv}"));
		findings.AddRange(Doctor.CheckState(stateDirectory, command));

		findings.Add(Directory.Exists(paths.TelemetryDirectory) && Directory.GetFiles(paths.TelemetryDirectory, "vspaneltelemetry_*.zip").Length > 0
			? new(Severity.Ok, $"Telemetry mod for new servers: {paths.TelemetryDirectory}")
			: new(Severity.Warning, $"No telemetry mod in {paths.TelemetryDirectory}: readiness checks for new servers are limited"));

		var leftovers = paths.LegacyProgram.Where(path => File.Exists(path) || Directory.Exists(path)).ToList();
		if (leftovers.Count > 0)
		{
			findings.Add(new(Severity.Warning, $"Legacy panel files remain: {string.Join(", ", leftovers)}. The new panel does not use them; remove them manually"));
		}

		return findings;
	}

	[SupportedOSPlatform("windows")]
	public static async Task<IReadOnlyList<Finding>> CheckServicesAsync(WindowsHost host)
	{
		ArgumentNullException.ThrowIfNull(host);
		var findings = new List<Finding>();
		foreach (var (name, expected) in new[] { (WindowsPaths.AgentService, host.Paths.AgentCommandLine), (WindowsPaths.PanelService, host.Paths.PanelCommandLine) })
		{
			var config = ServiceManager.Query(name);
			if (config is null)
			{
				findings.Add(new(Severity.Problem, $"Service {name} is not installed. Fix with {command} repair"));
				continue;
			}

			if (!WindowsPaths.IsServiceAccount(config.Account))
			{
				findings.Add(new(Severity.Problem, $"{name} runs as {config.Account} instead of LocalSystem. Fix with {command} repair"));
			}

			var status = WindowsHost.Status(name);
			findings.Add(status == ServiceControllerStatus.Running && config.AutoStart
				? new(Severity.Ok, $"{name}: running, automatic start, account {config.Account}")
				: new(Severity.Problem, $"{name}: {WindowsCommands.Describe(status)}{(config.AutoStart ? string.Empty : ", startup is not automatic")}. Start with {command} start; see {command} logs for the cause"));
			if (!string.Equals(config.CommandLine, expected, StringComparison.OrdinalIgnoreCase))
			{
				findings.Add(new(Severity.Problem, $"{name} does not launch the active version: {config.CommandLine}. Fix with {command} repair"));
			}
		}

		findings.Add(PipeExists(WindowsPaths.AgentPipe)
			? new(Severity.Ok, $"Agent pipe \\\\.\\pipe\\{WindowsPaths.AgentPipe}")
			: new(Severity.Problem, $"Missing agent pipe \\\\.\\pipe\\{WindowsPaths.AgentPipe}: the panel cannot execute commands"));

		findings.Add(CheckDataAccess(host.Paths.DataRoot));
		findings.Add(await CheckPanelPortAsync(host).ConfigureAwait(false));

		foreach (var task in WindowsPaths.LegacyTasks)
		{
			if ((await Shell.RunAsync("schtasks.exe", ["/Query", "/TN", task]).ConfigureAwait(false)).Succeeded)
			{
				findings.Add(new(Severity.Problem, $"Legacy scheduled task {task} remains: it will occupy the panel port at startup. Remove with {command} repair"));
			}
		}

		var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
		var runtimes = File.Exists(dotnet) ? await Shell.RunAsync(dotnet, ["--list-runtimes"]).ConfigureAwait(false) : null;
		findings.Add(runtimes is { Succeeded: true } && runtimes.Output.Contains("Microsoft.NETCore.App 10.", StringComparison.Ordinal)
			? new(Severity.Ok, ".NET 10 runtime for the game is installed")
			: new(Severity.Warning, $".NET 10 runtime not found in {Path.GetDirectoryName(dotnet)}: Vintage Story 1.22 servers will not start"));
		return findings;
	}

	/// <summary>Каталог данных не должен быть доступен на чтение всем: там ключ сессий и учётные записи.</summary>
	[SupportedOSPlatform("windows")]
	private static Finding CheckDataAccess(string dataRoot)
	{
		if (!Directory.Exists(dataRoot))
		{
			return new(Severity.Problem, $"Missing data directory {dataRoot}. Fix with {command} repair");
		}

		SecurityIdentifier[] broad =
		[
			new(WellKnownSidType.BuiltinUsersSid, null),
			new(WellKnownSidType.AuthenticatedUserSid, null),
			new(WellKnownSidType.WorldSid, null),
		];
		var rules = new DirectoryInfo(dataRoot).GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
		var open = rules.OfType<FileSystemAccessRule>()
			.Where(rule => rule.AccessControlType == AccessControlType.Allow && broad.Contains(rule.IdentityReference))
			.Select(rule => rule.IdentityReference.Translate(typeof(NTAccount)).Value)
			.Distinct(StringComparer.Ordinal)
			.ToList();
		return open.Count == 0
			? new(Severity.Ok, $"Data directory {dataRoot} is closed to other users")
			: new(Severity.Problem, $"Data directory {dataRoot} is accessible to {string.Join(", ", open)}: any machine user could read the session key. Fix with {command} repair");
	}

	[SupportedOSPlatform("windows")]
	private static async Task<Finding> CheckPanelPortAsync(WindowsHost host)
	{
		var address = PanelState.LocalAddress(host.PanelEnvironment());
		var uri = new Uri(address);
		using var client = new TcpClient();
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
		try
		{
			await client.ConnectAsync(uri.Host, uri.Port, timeout.Token).ConfigureAwait(false);
			return new(Severity.Ok, $"Panel responds at {address}");
		}
		catch (Exception exception) when (exception is SocketException or OperationCanceledException)
		{
			return new(Severity.Problem, $"Panel does not respond at {address}. See {command} logs for the cause");
		}
	}

	private static bool PipeExists(string name)
	{
		try
		{
			return Directory.GetFiles(@"\\.\pipe\").Any(pipe => string.Equals(Path.GetFileName(pipe), name, StringComparison.OrdinalIgnoreCase));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return false;
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
