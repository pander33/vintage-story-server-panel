using Panel.Fleet.Infrastructure;

namespace Panel.Cli;

/// <summary>
/// Шаг установки «Каталог игровых серверов»: где будут создаваться серверы, начиная с первого.
/// </summary>
/// <remarks>
/// <para>
/// Путь здесь не проверяется: его проверяет и готовит демон (<c>manage servers-root</c>), как и
/// при смене в настройках панели. Поэтому шаг выполняется после запуска демона и до создания
/// первого сервера.
/// </para>
/// <para>
/// Вопрос задаётся только при первой установке и только в терминале: <c>install.cmd</c>
/// двойным щелчком или <c>sudo bash install.sh</c>. При обновлении, <c>curl | bash</c> и
/// автоматической установке используется <c>--servers-dir</c>.
/// </para>
/// </remarks>
internal static class ServersRootStep
{
	/// <summary>
	/// Куда писать и откуда читать ответ; <see cref="Input"/> равен <see langword="null"/> вне
	/// терминала. <see cref="Browse"/> — окно выбора каталога (Windows); если его нет или оно не
	/// открылось, путь вводится текстом.
	/// </summary>
	public sealed record Terminal(
		TextWriter Output,
		TextReader? Input,
		Action<string> Step,
		Action<string> Warn,
		Func<string, Task<FolderPick>>? Browse = null);

	/// <summary>Заголовок окна выбора каталога.</summary>
	public const string BrowseTitle = "Каталог игровых серверов Vintage Story";

	/// <returns>
	/// Можно ли создавать первый сервер. Если явно названный каталог не принят, сервер не
	/// создаётся, чтобы он не оказался в другом месте.
	/// </returns>
	public static async Task<bool> RunAsync(
		InstallOptions options,
		string panelConfig,
		bool firstInstall,
		Terminal terminal,
		Func<IReadOnlyList<string>, Task<bool>> manage)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(terminal);
		ArgumentNullException.ThrowIfNull(manage);

		var current = Current(panelConfig);
		if (options.ServersDirectory.Length > 0)
		{
			terminal.Step("Каталог игровых серверов");
			if (await manage(["servers-root", options.ServersDirectory]).ConfigureAwait(false))
			{
				return true;
			}

			terminal.Warn("каталог серверов не изменён, и первый сервер не создан. Выберите каталог в панели («Настройки» → «Панель») и создайте сервер там.");
			return false;
		}

		if (!firstInstall || terminal.Input is null)
		{
			return true;
		}

		terminal.Step("Каталог игровых серверов");
		terminal.Output.WriteLine("Здесь будут программа, миры, моды и резервные копии каждого сервера. Потом его можно сменить в панели: «Настройки» → «Панель».");
		if (terminal.Browse is { } browse)
		{
			terminal.Output.WriteLine($"Выберите каталог в открывшемся окне; новую папку можно создать там же. «Отмена» оставит {current}.");
			var start = current;
			while (true)
			{
				var pick = await browse(start).ConfigureAwait(false);
				if (!pick.Shown)
				{
					terminal.Output.WriteLine("Окно выбора не открылось — введите путь.");
					break;
				}

				if (string.IsNullOrEmpty(pick.Path) || string.Equals(pick.Path, current, StringComparison.OrdinalIgnoreCase))
				{
					terminal.Output.WriteLine($"Каталог серверов: {current}");
					return true;
				}

				terminal.Output.WriteLine($"Выбран {pick.Path}");
				if (await manage(["servers-root", pick.Path]).ConfigureAwait(false))
				{
					return true;
				}

				terminal.Output.WriteLine($"Этот каталог не подошёл (причина выше) — выберите другой или нажмите «Отмена», чтобы оставить {current}.");
				start = pick.Path;
			}
		}

		while (true)
		{
			terminal.Output.Write($"Каталог [{current}]: ");
			terminal.Output.Flush();
			var answer = terminal.Input.ReadLine()?.Trim().Trim('"');
			if (string.IsNullOrEmpty(answer) || string.Equals(answer, current, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}

			if (await manage(["servers-root", answer]).ConfigureAwait(false))
			{
				return true;
			}

			terminal.Output.WriteLine($"Введите другой каталог или нажмите Enter, чтобы оставить {current}.");
		}
	}

	/// <summary>Текущий каталог серверов из <c>vs-panel.json</c>; пусто, если файл не прочитался.</summary>
	private static string Current(string panelConfig)
	{
		try
		{
			var parsed = PanelConfiguration.Parse(File.ReadAllText(panelConfig));
			return parsed.IsSuccess ? parsed.Value.InstancesRoot : string.Empty;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return string.Empty;
		}
	}
}
