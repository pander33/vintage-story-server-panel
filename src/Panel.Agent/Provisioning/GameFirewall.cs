using System.Globalization;
using Panel.Fleet.Infrastructure;
using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Provisioning;

/// <summary>Правила брандмауэра для игровых портов.</summary>
public interface IGameFirewall
{
	/// <summary>Открыть порт игрокам.</summary>
	/// <returns>Текст для оператора, если открыть не удалось; иначе <see langword="null"/>.</returns>
	Task<Phrase?> OpenAsync(int port, CancellationToken ct);

	/// <summary>Снять правило порта. Ошибки не мешают: правило без сервера ничего не ломает.</summary>
	Task CloseAsync(int port, CancellationToken ct);
}

/// <summary>
/// Игровой порт в брандмауэре машины: ufw на Linux, брандмауэр Windows через <c>netsh</c>
/// (правила — <see cref="GamePortRule"/>).
/// </summary>
/// <remarks>
/// <para>
/// Брандмауэр не трогается, если оператор взял правила на себя (<c>PANEL_FIREWALL=off</c>), а на
/// Linux ещё и если ufw не установлен или выключен: правил, которые никто не применяет, не заводим.
/// Другой брандмауэр (firewalld, nftables) и брандмауэр провайдера VPS панель не видит — это
/// сказано в README.
/// </para>
/// <para>
/// Файл окружения читается при каждом вызове: <c>vs-panel repair --firewall</c> меняет его без
/// перезапуска демона.
/// </para>
/// </remarks>
public sealed class GameFirewall : IGameFirewall
{
	private const string ufw = "/usr/sbin/ufw";

	private static readonly TimeSpan timeout = TimeSpan.FromSeconds(30);

	private readonly string panelConfigPath;

	public GameFirewall(string panelConfigPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(panelConfigPath);
		this.panelConfigPath = panelConfigPath;
	}

	public async Task<Phrase?> OpenAsync(int port, CancellationToken ct)
	{
		var text = port.ToString(CultureInfo.InvariantCulture);
		if (!GamePortRule.IsValid(port))
		{
			return Phrase.Format($"Игровой порт {text} не открыт в брандмауэре: допустимы порты от 1024 до 65535");
		}

		if (await ToolAsync(ct).ConfigureAwait(false) is not { } tool)
		{
			return null;
		}

		if (tool == Tool.Netsh)
		{
			// Правило с тем же именем сначала снимается, чтобы повтор не множил копии.
			foreach (var rule in GamePortRule.NetshDelete(port))
			{
				await HostTools.RunAsync(Netsh, rule, timeout, ct).ConfigureAwait(false);
			}
		}

		var rules = tool == Tool.Netsh ? GamePortRule.NetshAdd(port) : GamePortRule.UfwAllow(port);
		foreach (var rule in rules)
		{
			var added = await HostTools.RunAsync(tool == Tool.Netsh ? Netsh : ufw, rule, timeout, ct).ConfigureAwait(false);
			if (added.ExitCode != 0)
			{
				return tool == Tool.Netsh
					? Phrase.Format($"Игровой порт {text} в брандмауэре Windows не открыт: {added.Tail("netsh отказал")}")
					: Phrase.Format($"Игровой порт {text} в ufw не открыт: {added.Tail("ufw отказал")}");
			}
		}

		return null;
	}

	public async Task CloseAsync(int port, CancellationToken ct)
	{
		if (!GamePortRule.IsValid(port) || await ToolAsync(ct).ConfigureAwait(false) is not { } tool)
		{
			return;
		}

		var rules = tool == Tool.Netsh ? GamePortRule.NetshDelete(port) : GamePortRule.UfwDelete(port);
		foreach (var rule in rules)
		{
			await HostTools.RunAsync(tool == Tool.Netsh ? Netsh : ufw, rule, timeout, ct).ConfigureAwait(false);
		}
	}

	private static string Netsh => Path.Combine(Environment.SystemDirectory, "netsh.exe");

	/// <summary>Чем ставить правила; <see langword="null"/> — брандмауэр не трогается.</summary>
	private async Task<Tool?> ToolAsync(CancellationToken ct)
	{
		if (await FirewallScopeAsync(ct).ConfigureAwait(false) == FirewallScope.Off)
		{
			return null;
		}

		if (OperatingSystem.IsWindows())
		{
			return Tool.Netsh;
		}

		if (!File.Exists(ufw))
		{
			return null;
		}

		var status = await HostTools.RunAsync(ufw, ["status"], timeout, ct).ConfigureAwait(false);
		return status.ExitCode == 0 && status.StandardOutput.Contains("Status: active", StringComparison.Ordinal) ? Tool.Ufw : null;
	}

	/// <summary><c>PANEL_FIREWALL</c> из файла окружения; нечитаемый файл — умолчание <c>lan</c>, то есть «трогать».</summary>
	private async Task<FirewallScope> FirewallScopeAsync(CancellationToken ct)
	{
		try
		{
			var configuration = PanelConfiguration.Parse(await File.ReadAllTextAsync(panelConfigPath, ct).ConfigureAwait(false));
			if (!configuration.IsSuccess || configuration.Value.PanelEnvFile.Length == 0)
			{
				return FirewallScope.Lan;
			}

			var lines = await File.ReadAllLinesAsync(configuration.Value.PanelEnvFile, ct).ConfigureAwait(false);
			return PanelListener.FromEnvironment(ProvisionBackend.EnvironmentValues(lines)).Firewall;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return FirewallScope.Lan;
		}
	}

	private enum Tool
	{
		Ufw,
		Netsh,
	}
}
