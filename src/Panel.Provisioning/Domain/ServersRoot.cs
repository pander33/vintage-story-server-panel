using Panel.SharedKernel;

namespace Panel.Provisioning.Domain;

/// <summary>
/// Каталог, в котором создаются новые серверы: <c>instances_root</c> из <c>vs-panel.json</c>,
/// выбранный оператором при установке или в настройках панели.
/// </summary>
/// <remarks>
/// <para>
/// Путь проверяется по правилу <see cref="InstanceLayout"/>: абсолютный, из безопасных символов,
/// без пробелов. Кроме того, это не может быть системный каталог, каталог самой панели или
/// каталог, внутри которого лежит её состояние. Панель проверяет выбор, чтобы дать понятный
/// ответ, но демон полагается только на свою проверку.
/// </para>
/// <para>
/// Проверка не обращается к файловой системе. Владельца и списки доступа предков проверяет
/// демон: это уже состояние машины, а не правило для пути.
/// </para>
/// </remarks>
public sealed record ServersRoot
{
	private ServersRoot(string path, PlatformLayout layout)
	{
		Path = path;
		Layout = layout;
	}

	public string Path { get; }

	public PlatformLayout Layout { get; }

	/// <summary>Системные каталоги верхнего уровня: запрещены и сами, и всё, что под ними.</summary>
	private static readonly string[] linuxSystem =
	[
		"/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/libx32", "/proc", "/root", "/run",
		"/sbin", "/sys", "/tmp", "/usr", "/var/tmp", "/var/run", "/var/lock", "/opt/vs-panel", "/var/lib/vs-panel",
	];

	/// <summary>Каталоги, которые сами не могут быть каталогом серверов, но могут его содержать.</summary>
	private static readonly string[] linuxShared = ["/home", "/mnt", "/media", "/opt", "/srv", "/var", "/var/lib"];

	/// <summary>Первые сегменты после буквы диска, под которыми серверы создавать нельзя.</summary>
	private static readonly string[] windowsSystem = ["Windows", "Users", "Recovery", "PerfLogs", "Boot", "EFI"];

	/// <summary>Каталоги диска, которые сами не могут быть каталогом серверов.</summary>
	private static readonly string[] windowsShared = ["ProgramData", "Program Files", "Program Files (x86)"];

	/// <param name="raw">Путь, как его ввёл оператор.</param>
	/// <param name="layout">Раскладка панели: разделитель и правило пути.</param>
	/// <param name="exclusive">
	/// Каталоги панели, с которыми каталог серверов не должен пересекаться: выполнения, staging,
	/// мода телеметрии.
	/// </param>
	/// <param name="containers">
	/// Каталоги, внутри которых каталог серверов может лежать, но не может с ними совпадать или
	/// содержать их: каталог данных панели на Windows, каталоги реестра и окружения.
	/// </param>
	public static Result<ServersRoot> Create(
		string? raw,
		PlatformLayout layout,
		IEnumerable<string> exclusive,
		IEnumerable<string> containers)
	{
		ArgumentNullException.ThrowIfNull(exclusive);
		ArgumentNullException.ThrowIfNull(containers);

		var windows = layout == PlatformLayout.Windows;
		var text = (raw ?? string.Empty).Trim();
		if (text.Length == 0)
		{
			return Failure.Invalid("Укажите каталог для серверов");
		}

		if (InstanceLayout.NormalizeRoot(text, layout) is not { } path)
		{
			return Failure.Invalid(windows
				? $"Каталог серверов «{text}» не подходит: нужен полный путь с буквой диска из латинских букв, цифр, точки, дефиса и подчёркивания, без пробелов — например D:\\vs-servers"
				: $"Каталог серверов «{text}» не подходит: нужен абсолютный путь из латинских букв, цифр, точки, дефиса и подчёркивания, без пробелов — например /data/vs-servers");
		}

		var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		var separator = windows ? '\\' : '/';

		bool Within(string inner, string outer) =>
			string.Equals(inner, outer, comparison) || inner.StartsWith(outer.TrimEnd(separator) + separator, comparison);

		if (windows)
		{
			var segments = path.Split('\\');
			if (windowsSystem.Any(name => string.Equals(segments[1], name, StringComparison.OrdinalIgnoreCase))
				|| (segments.Length == 2 && windowsShared.Any(name => string.Equals(segments[1], name, StringComparison.OrdinalIgnoreCase))))
			{
				return Failure.Invalid($"В {path} серверы не создаются: это системный каталог Windows. Выберите отдельный каталог, например {segments[0]}\\vs-servers");
			}
		}
		else if (linuxSystem.Any(system => Within(path, system)) || linuxShared.Contains(path, StringComparer.Ordinal))
		{
			return Failure.Invalid($"В {path} серверы не создаются: это системный каталог. Выберите отдельный каталог, например /srv/vs-panel");
		}

		foreach (var reserved in exclusive.Select(item => InstanceLayout.NormalizeRoot(item, layout)).OfType<string>())
		{
			if (Within(path, reserved) || Within(reserved, path))
			{
				return Failure.Invalid($"Каталог серверов не может пересекаться со служебным каталогом панели {reserved}");
			}
		}

		foreach (var container in containers.Select(item => InstanceLayout.NormalizeRoot(item, layout)).OfType<string>())
		{
			if (Within(container, path))
			{
				return Failure.Invalid($"Каталог серверов не может совпадать с каталогом панели {container} или вмещать его: выберите каталог внутри него или отдельный");
			}
		}

		return new ServersRoot(path, layout);
	}

	/// <summary>
	/// Каталог, уже записанный в настройках панели, без проверки выбора, только для сравнения.
	/// Для небезопасного пути <see langword="null"/>.
	/// </summary>
	public static ServersRoot? FromConfiguration(string? path, PlatformLayout layout) =>
		InstanceLayout.NormalizeRoot(path ?? string.Empty, layout) is { } normalized ? new ServersRoot(normalized, layout) : null;

	/// <summary>Тот же ли это каталог; на Windows регистр букв не учитывается.</summary>
	public bool Is(ServersRoot? other) =>
		other is not null
		&& other.Layout == Layout
		&& string.Equals(other.Path, Path, Layout == PlatformLayout.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

	/// <summary>Родительский каталог. Он должен существовать: промежуточные каталоги демон не создаёт.</summary>
	public string Parent
	{
		get
		{
			var separator = Layout == PlatformLayout.Windows ? '\\' : '/';
			var cut = Path.LastIndexOf(separator);
			return Layout == PlatformLayout.Windows
				? (cut <= 2 ? Path[..2] + "\\" : Path[..cut])
				: (cut <= 0 ? "/" : Path[..cut]);
		}
	}
}
