using System.Net;
using System.Net.NetworkInformation;
using Panel.Provisioning.Domain;

namespace Panel.Cli;

/// <summary>Итог работы с брандмауэром: что сказать оператору и было ли это отказом.</summary>
internal sealed record FirewallOutcome(string Message, bool Failed);

/// <summary>
/// Доступ к панели со стороны установщика: строки <c>vs-panel.env</c>, заданные при установке,
/// правило брандмауэра для порта панели и адреса, которые показываются оператору.
/// </summary>
/// <remarks>
/// Что открывать и кому, решает <see cref="PanelListener"/> — то же суждение, что у демона при
/// смене адреса в мастере установки. Здесь только запуск <c>ufw</c> и <c>netsh</c>.
/// </remarks>
internal static class PanelAccess
{
	/// <summary>
	/// Правила ufw для порта панели. Если адрес, порт или охват изменились, прежние правила
	/// снимаются. При неактивном ufw ничего не делается: правил, которые он не применяет, не
	/// заводим.
	/// </summary>
	/// <param name="previous">Настройки до установки; <see langword="null"/> — файла не было.</param>
	public static async Task<FirewallOutcome?> ApplyUfwAsync(PanelListener? previous, PanelListener current)
	{
		ArgumentNullException.ThrowIfNull(current);
		var status = await Shell.RunAsync("ufw", ["status"]).ConfigureAwait(false);
		if (!status.Succeeded || !status.Output.Contains("Status: active", StringComparison.Ordinal))
		{
			return current.OpensFirewall
				? new FirewallOutcome("ufw не активен — правило для порта панели не нужно. Если брандмауэр другой, откройте порт в нём.", false)
				: null;
		}

		if (previous is not null && previous != current)
		{
			foreach (var rule in previous.UfwDelete)
			{
				await Shell.RunAsync("ufw", rule).ConfigureAwait(false);
			}
		}

		foreach (var rule in current.UfwAllow)
		{
			var added = await Shell.RunAsync("ufw", rule).ConfigureAwait(false);
			if (!added.Succeeded)
			{
				return new FirewallOutcome($"порт панели {current.Port} в ufw не открыт: {added.Reason}", true);
			}
		}

		return Describe(previous, current, "ufw");
	}

	/// <summary>
	/// Правило брандмауэра Windows для порта панели. Правило одно, по имени, поэтому прежнее
	/// снимается всегда, каким бы ни был прежний порт.
	/// </summary>
	public static async Task<FirewallOutcome?> ApplyWindowsAsync(PanelListener? previous, PanelListener current)
	{
		ArgumentNullException.ThrowIfNull(current);
		await Shell.RunAsync("netsh.exe", PanelListener.NetshDelete).ConfigureAwait(false);
		if (current.NetshAdd is { } rule)
		{
			var added = await Shell.RunAsync("netsh.exe", rule).ConfigureAwait(false);
			if (!added.Succeeded)
			{
				return new FirewallOutcome($"порт панели {current.Port} в брандмауэре не открыт: {added.Reason}", true);
			}
		}

		return Describe(previous, current, "брандмауэре Windows");
	}

	/// <summary>Снять правила панели при удалении: порт больше никто не слушает.</summary>
	public static async Task RemoveAsync(PanelListener current, bool windows)
	{
		ArgumentNullException.ThrowIfNull(current);
		if (windows)
		{
			await Shell.RunAsync("netsh.exe", PanelListener.NetshDelete).ConfigureAwait(false);
			return;
		}

		foreach (var rule in current.UfwDelete)
		{
			await Shell.RunAsync("ufw", rule).ConfigureAwait(false);
		}
	}

	private static FirewallOutcome? Describe(PanelListener? previous, PanelListener current, string where)
	{
		if (current.OpensFirewall)
		{
			return new FirewallOutcome($"В {where} открыт порт панели {current.Port} (TCP): {current.FirewallDescription}.", false);
		}

		if (current.Published && current.Firewall == FirewallScope.Off)
		{
			return new FirewallOutcome($"Порт панели {current.Port} в брандмауэре не открывается (PANEL_FIREWALL=off): правила ведёте вы.", false);
		}

		return previous?.OpensFirewall == true
			? new FirewallOutcome("Правило брандмауэра для панели снято: панель слушает только эту машину.", false)
			: null;
	}

	/// <summary>
	/// Файл окружения с заданными строками: строка с ключом заменяется, отсутствующая
	/// дописывается. Остальные строки, комментарии и порядок не меняются.
	/// </summary>
	public static string Rewrite(string text, IReadOnlyDictionary<string, string> values)
	{
		ArgumentNullException.ThrowIfNull(values);
		var lines = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n').ToList();
		if (lines is [""])
		{
			lines.Clear();
		}

		foreach (var (key, value) in values)
		{
			var index = lines.FindIndex(line => line.TrimStart().StartsWith(key + "=", StringComparison.Ordinal));
			if (index >= 0)
			{
				lines[index] = $"{key}={value}";
			}
			else
			{
				lines.Add($"{key}={value}");
			}
		}

		return string.Join("\n", lines) + "\n";
	}

	/// <summary>Адреса интерфейсов машины, которые подняты; при ошибке опроса — ни одного.</summary>
	public static IReadOnlyList<IPAddress> MachineAddresses()
	{
		try
		{
			return
			[
				.. NetworkInterface.GetAllNetworkInterfaces()
					.Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
					.SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
					.Select(unicast => unicast.Address),
			];
		}
		catch (NetworkInformationException)
		{
			return [];
		}
	}

	/// <summary>
	/// Куда идти за панелью: адрес на этой машине, адреса в сети, охват брандмауэра и
	/// предупреждение, если пароль пойдёт по сети открытым текстом.
	/// </summary>
	/// <param name="tunnel">Linux: подсказать SSH-туннель, если панель слушает только петлю.</param>
	public static void Print(TextWriter output, IReadOnlyDictionary<string, string> environment, IReadOnlyList<IPAddress> machine, bool tunnel)
	{
		ArgumentNullException.ThrowIfNull(output);
		var listener = PanelListener.FromEnvironment(environment);
		output.WriteLine($"Панель слушает {listener.Bind}:{listener.Port}.");
		output.WriteLine($"  на этой машине: {listener.LocalUrl}");
		if (!listener.Published)
		{
			if (tunnel)
			{
				output.WriteLine($"  с другого компьютера — через туннель: ssh -L {listener.Port}:127.0.0.1:{listener.Port} {Environment.GetEnvironmentVariable("SUDO_USER") ?? "user"}@{Environment.MachineName}");
			}

			output.WriteLine("  открыть доступ из сети: vs-panel repair --access lan (или internet)");
			return;
		}

		var urls = listener.NetworkUrls(machine);
		foreach (var url in urls)
		{
			output.WriteLine($"  из сети: {url}");
		}

		if (urls.Count == 0)
		{
			output.WriteLine("  из сети: по адресу этой машины в сети (адреса интерфейсов не определены)");
		}

		output.WriteLine($"  брандмауэр: {listener.FirewallDescription}");
		if (listener.Firewall == FirewallScope.Internet)
		{
			output.WriteLine($"  из интернета: по внешнему адресу машины; за роутером пробросьте на неё порт {listener.Port}/tcp");
		}

		if (EnvironmentFile.Value(environment, "PANEL_SECURE_COOKIE", "true") is "false" or "0" or "no")
		{
			output.WriteLine("Внимание: панель работает по HTTP — пароль и сессия идут по сети открытым текстом.");
			if (listener.Firewall == FirewallScope.Internet)
			{
				output.WriteLine("Для доступа из интернета поставьте перед панелью HTTPS-прокси и переустановите с --secure-cookie true.");
			}
		}
	}
}
