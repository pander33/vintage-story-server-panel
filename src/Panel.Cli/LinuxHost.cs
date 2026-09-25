using System.Globalization;
using System.Runtime.Versioning;

namespace Panel.Cli;

/// <summary>
/// Операции над машиной для установки и управления: systemd, учётная запись панели, права
/// файлов, команды панели от её имени.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxHost
{
	private static readonly TimeSpan quick = TimeSpan.FromMinutes(2);

	public LinuxHost(InstallPaths paths, TextWriter output)
	{
		Paths = paths;
		Output = output;
	}

	public InstallPaths Paths { get; }

	public TextWriter Output { get; }

	public static bool IsRoot => Environment.UserName == "root";

	public void Step(string title) => Output.WriteLine($"\n==> {title}");

	public void Warn(string message) => Console.Error.WriteLine($"Предупреждение: {message}");

	public Task<ShellResult> SystemctlAsync(params string[] arguments) => Shell.RunAsync("systemctl", arguments, quick);

	public async Task<string> UnitPropertyAsync(string unit, string property) =>
		(await SystemctlAsync("show", unit, "-p", property, "--value").ConfigureAwait(false)).Output.Trim();

	public async Task<bool> IsActiveAsync(string unit) =>
		(await SystemctlAsync("is-active", "--quiet", unit).ConfigureAwait(false)).Succeeded;

	/// <summary>uid учётной записи; <c>null</c>, если её нет.</summary>
	public static async Task<int?> UidAsync(string user)
	{
		var id = await Shell.RunAsync("id", ["-u", user], quick).ConfigureAwait(false);
		return id.Succeeded && int.TryParse(id.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ? uid : null;
	}

	/// <summary>Служебная учётная запись панели: без входа, домашний каталог — каталог состояния.</summary>
	public async Task<int> EnsurePanelUserAsync()
	{
		if (await UidAsync(InstallPaths.PanelUser).ConfigureAwait(false) is { } existing)
		{
			return existing;
		}

		var created = await Shell.RunAsync(
				"useradd",
				["--system", "--home-dir", Paths.StateDirectory, "--create-home", "--shell", "/usr/sbin/nologin", InstallPaths.PanelUser],
				quick)
			.ConfigureAwait(false);
		if (!created.Succeeded)
		{
			throw new InstallFailed($"Учётная запись {InstallPaths.PanelUser} не создана: {created.Reason}");
		}

		return await UidAsync(InstallPaths.PanelUser).ConfigureAwait(false)
			?? throw new InstallFailed($"Учётная запись {InstallPaths.PanelUser} создана, но не находится");
	}

	public static void CreateDirectory(string path, UnixFileMode mode)
	{
		Directory.CreateDirectory(path);
		File.SetUnixFileMode(path, mode);
	}

	/// <summary>Запись во временный файл и переименование: читатель видит либо старое, либо новое содержимое.</summary>
	public static void WriteFile(string path, string content, UnixFileMode mode)
	{
		var temporary = path + ".new";
		File.WriteAllText(temporary, content.Replace("\r\n", "\n", StringComparison.Ordinal), new System.Text.UTF8Encoding(false));
		File.SetUnixFileMode(temporary, mode);
		File.Move(temporary, path, overwrite: true);
	}

	public static void CopyFile(string source, string destination, UnixFileMode mode)
	{
		File.Copy(source, destination, overwrite: true);
		File.SetUnixFileMode(destination, mode);
	}

	public async Task ChownAsync(string owner, string path, bool recursive = false)
	{
		var arguments = recursive ? new[] { "-R", owner, path } : [owner, path];
		var changed = await Shell.RunAsync("chown", arguments, quick).ConfigureAwait(false);
		if (!changed.Succeeded)
		{
			throw new InstallFailed($"Владелец {path} не изменён: {changed.Reason}");
		}
	}

	/// <summary>
	/// Атомарная замена ссылки: новая создаётся рядом, <c>mv -T</c> переименовывает её поверх
	/// старой одним <c>rename(2)</c> (§3 заметки). Юнит, стартующий в этот момент, видит либо
	/// прежнюю версию, либо новую.
	/// </summary>
	public static async Task ReplaceLinkAsync(string link, string target)
	{
		var temporary = link + ".new";
		var made = await Shell.RunAsync("ln", ["-sfn", target, temporary], quick).ConfigureAwait(false);
		var moved = made.Succeeded ? await Shell.RunAsync("mv", ["-Tf", temporary, link], quick).ConfigureAwait(false) : made;
		if (!moved.Succeeded)
		{
			throw new InstallFailed($"Ссылка {link} → {target} не заменена: {moved.Reason}");
		}
	}

	/// <summary>Куда указывает ссылка; <c>null</c>, если это не ссылка или её нет.</summary>
	public static string? LinkTarget(string link)
	{
		try
		{
			return new FileInfo(link).LinkTarget;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>Окружение панели для её собственных команд: ключи <c>PANEL_*</c> из <c>vs-panel.env</c>.</summary>
	public Dictionary<string, string> PanelEnvironment() =>
		EnvironmentFile.Load(Paths.PanelEnv)
			.Where(pair => pair.Key.StartsWith("PANEL_", StringComparison.Ordinal))
			.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

	/// <summary>
	/// <c>vs-panel-web manage …</c> от имени учётной записи панели, чтобы файлы состояния
	/// оставались её файлами.
	/// </summary>
	public async Task<int> ManageAsync(IEnumerable<string> command, bool attached = false)
	{
		var arguments = new List<string> { "-u", InstallPaths.PanelUser, "--", "env" };
		arguments.AddRange(PanelEnvironment().Select(pair => $"{pair.Key}={pair.Value}"));
		arguments.Add(Paths.CurrentBinary("vs-panel-web"));
		arguments.Add("manage");
		arguments.AddRange(command);

		if (attached)
		{
			return await Shell.RunAttachedAsync("runuser", arguments).ConfigureAwait(false);
		}

		// Вывод показывается сразу: создание сервера занимает минуты и сообщает о каждом шаге.
		var result = await Shell.RunAsync("runuser", arguments, TimeSpan.FromMinutes(30), echo: Output).ConfigureAwait(false);

		return result.ExitCode;
	}

	/// <summary>
	/// Дождаться нужного состояния юнита, показывая ход ожидания. Истёкшее ожидание не считается
	/// ошибкой: панель может завершать обновление, и прерывать её нельзя.
	/// </summary>
	public async Task<bool> WaitForStartAsync(string unit, string oldPid, int seconds)
	{
		var elapsed = 0;
		while (elapsed < seconds)
		{
			var state = await UnitPropertyAsync(unit, "ActiveState").ConfigureAwait(false);
			var pid = await UnitPropertyAsync(unit, "MainPID").ConfigureAwait(false);
			if (state == "active" && pid is not ("" or "0") && (oldPid is "" or "0" || pid != oldPid))
			{
				Output.WriteLine($"{unit} запущен: PID {pid}.");
				return true;
			}

			if (state == "failed")
			{
				await Console.Error.WriteLineAsync($"Не удалось запустить {unit}.").ConfigureAwait(false);
				await Shell.RunAttachedAsync("systemctl", ["status", unit, "--no-pager", "-l"]).ConfigureAwait(false);
				return false;
			}

			Output.WriteLine($"Ожидание запуска {unit}: {(state.Length > 0 ? state : "unknown")}, прошло {elapsed} с...");
			await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
			elapsed += 5;
		}

		await Console.Error.WriteLineAsync($"\nЗапуск передан systemd, но не завершился за {seconds} с.").ConfigureAwait(false);
		await Console.Error.WriteLineAsync("Операция не была принудительно прервана: панель может безопасно завершать обновление или резервную копию.").ConfigureAwait(false);
		await Console.Error.WriteLineAsync($"Проверьте: systemctl status {unit} --no-pager -l").ConfigureAwait(false);
		await Console.Error.WriteLineAsync($"И журнал:  journalctl -u {unit} -n 100 --no-pager").ConfigureAwait(false);
		return false;
	}

	/// <summary>Сколько серверов в реестре; <c>null</c>, если реестр не читается.</summary>
	public int? RegisteredServers()
	{
		try
		{
			var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Paths.Registry));
			return (node?["instances"] as System.Text.Json.Nodes.JsonObject)?.Count ?? 0;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
		{
			return null;
		}
	}

	/// <summary>Завершён ли мастер установки: <c>completed</c> в <c>setup.json</c>.</summary>
	public bool SetupCompleted()
	{
		try
		{
			var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(StateDirectory(), "setup.json")));
			return node?["completed"] is System.Text.Json.Nodes.JsonValue flag && flag.TryGetValue<bool>(out var done) && done;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
		{
			return false;
		}
	}

	/// <summary>Каталог состояния из <c>vs-panel.env</c>, если он там переопределён.</summary>
	public string StateDirectory() =>
		EnvironmentFile.Value(EnvironmentFile.Load(Paths.PanelEnv), "PANEL_STATE_DIR", Paths.StateDirectory);
}

/// <summary>Шаг установки не удался; текст сообщения для оператора.</summary>
internal sealed class InstallFailed(string message) : Exception(message);
