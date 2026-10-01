using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Agent.Content;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Provisioning;

/// <summary>Окружение панели, каталог новых серверов и платформенные операции панели.</summary>
internal sealed class PanelEnvironmentBackend(ProvisioningConfiguration settings, IProvisioningPlatform platform)
{
	private static readonly UnixFileMode mode644 =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

	private static readonly UnixFileMode mode640 =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

	private static readonly JsonSerializerOptions configJson = new()
	{
		WriteIndented = true,
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	public Task<RuntimeProbePayload> ProbeRuntimeAsync(CancellationToken ct) => platform.ProbeRuntimeAsync(ct);

	/// <summary>
	/// Свободное место там, где будут создаваться серверы: на ближайшем существующем предке
	/// <c>instances_root</c>.
	/// </summary>
	public async Task<Result<DiskFreePayload>> DiskFreeAsync(CancellationToken ct)
	{
		var configuration = await settings.ReadAsync(ct).ConfigureAwait(false);
		if (!configuration.IsSuccess)
		{
			return configuration.Failure!;
		}

		var root = configuration.Value.InstancesRoot;
		var target = new DirectoryInfo(root);
		while (!target.Exists && target.Parent is { } parent)
		{
			target = parent;
		}

		var usage = GameFiles.Usage(target.FullName);
		return new DiskFreePayload
		{
			Path = target.FullName,
			Total = usage.Total,
			Used = usage.Used,
			Free = usage.Free,
			InstancesRoot = root,
		};
	}

	/// <summary>Журнал службы панели, общий для всех серверов.</summary>
	public Task<Result<PanelLogsPayload>> PanelLogsAsync(string lines, CancellationToken ct)
	{
		if (!int.TryParse(lines.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var requested))
		{
			return Task.FromResult<Result<PanelLogsPayload>>(Failure.Invalid("Invalid line count"));
		}

		return platform.PanelLogsAsync(Math.Clamp(requested, 20, 2000), ct);
	}

	/// <summary>Одна строка из разрешённых шести; файл root:root 0640.</summary>
	public async Task<Result<PanelEnvironmentSetPayload>> SetPanelEnvironmentAsync(PanelSetting setting, CancellationToken ct)
	{
		var configuration = await settings.ReadAsync(ct).ConfigureAwait(false);
		if (!configuration.IsSuccess)
		{
			return configuration.Failure!;
		}

		var path = configuration.Value.PanelEnvFile;
		string[] lines;
		try
		{
			var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
			lines = text.Split('\n');
			if (text.EndsWith('\n'))
			{
				lines = lines[..^1];
			}

			// Как splitlines(): \r тоже срезается, CRLF в файле окружения считается поломкой.
			lines = [.. lines.Select(line => line.TrimEnd('\r'))];
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Cannot read panel environment: {exception.Message}");
		}

		var rewritten = setting.Rewrite(lines);
		var written = await ProvisioningFiles.WriteAtomicallyAsync(path, rewritten, mode640, ct).ConfigureAwait(false);
		if (!written.IsSuccess)
		{
			return written.Failure!;
		}

		// Мастер меняет адрес и порт по одному ключу; правило брандмауэра следует за каждым, чтобы
		// после перезапуска панель была видна там же, где её открыли при установке.
		Phrase? warning = null;
		if (setting.Key is "PANEL_BIND" or "PANEL_PORT")
		{
			warning = await platform.SyncPanelFirewallAsync(
					PanelListener.FromEnvironment(EnvironmentValues(lines)),
					PanelListener.FromEnvironment(EnvironmentValues(rewritten.Split('\n'))),
					ct)
				.ConfigureAwait(false);
		}

		return new PanelEnvironmentSetPayload { Key = setting.Key, FirewallWarning = warning };
	}

	/// <summary>Строки <c>KEY=VALUE</c> файла окружения; комментарии и пустые строки пропускаются.</summary>
	internal static Dictionary<string, string> EnvironmentValues(IEnumerable<string> lines)
	{
		var values = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var line in lines.Select(item => item.Trim()))
		{
			var separator = line.IndexOf('=', StringComparison.Ordinal);
			if (separator > 0 && !line.StartsWith('#'))
			{
				values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
			}
		}

		return values;
	}

	/// <summary>
	/// Каталог новых серверов (новое в 2.0): проверка пути, предков и самого каталога, затем
	/// запись <c>instances_root</c> в <c>vs-panel.json</c>.
	/// </summary>
	/// <remarks>
	/// Созданные серверы не переезжают: их пути записаны в реестре. Прежний каталог попадает в
	/// <c>former_instances_roots</c>, его серверы по-прежнему считаются созданными панелью, их
	/// можно удалить и переустановить (<see cref="ProvisioningConfiguration.CreatedLayout"/>).
	/// </remarks>
	public async Task<Result<InstancesRootSetPayload>> SetInstancesRootAsync(string raw, CancellationToken ct)
	{
		var configuration = await settings.ReadAsync(ct).ConfigureAwait(false);
		if (!configuration.IsSuccess)
		{
			return configuration.Failure!;
		}

		var current = configuration.Value;
		if (current.IsReadOnlyRegistry)
		{
			return Failure.Conflict("Каталог серверов меняется только в настройках панели версии 2: переустановите панель");
		}

		if (current.Layout != platform.Layout)
		{
			return Failure.Unavailable("Раскладка в настройках панели не совпадает с платформой демона");
		}

		var root = settings.Choose(current, raw);
		if (!root.IsSuccess)
		{
			return root.Failure!;
		}

		var chosen = root.Value;
		var owned = Same(chosen, current.InstancesRoot) || current.FormerInstancesRoots.Any(path => Same(chosen, path));
		var secured = await platform.SecureServersRootAsync(chosen, owned, ct).ConfigureAwait(false);
		if (!secured.IsSuccess)
		{
			return secured.Failure!;
		}

		if (!Same(chosen, current.InstancesRoot))
		{
			string text;
			try
			{
				text = await File.ReadAllTextAsync(settings.Path, ct).ConfigureAwait(false);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				return Failure.Unavailable($"Не читается {settings.Path}: {exception.Message}");
			}

			var rewritten = RewriteInstancesRoot(text, chosen, current.InstancesRoot);
			if (!rewritten.IsSuccess)
			{
				return rewritten.Failure!;
			}

			var written = await ProvisioningFiles.WriteAtomicallyAsync(settings.Path, rewritten.Value, mode644, ct).ConfigureAwait(false);
			if (!written.IsSuccess)
			{
				return written.Failure!;
			}
		}

		return new InstancesRootSetPayload { InstancesRoot = chosen.Path, Previous = current.InstancesRoot };
	}

	/// <summary>
	/// Текст <c>vs-panel.json</c> с новым <c>instances_root</c>. Остальные ключи и их порядок
	/// сохраняются; прежний каталог добавляется в <c>former_instances_roots</c>, новый оттуда
	/// убирается.
	/// </summary>
	public static Result<string> RewriteInstancesRoot(string text, ServersRoot chosen, string previous)
	{
		ArgumentNullException.ThrowIfNull(chosen);
		ArgumentNullException.ThrowIfNull(previous);
		JsonObject document;
		try
		{
			if (JsonNode.Parse(text) is not JsonObject parsed)
			{
				return Failure.Invalid("Настройки панели не являются объектом JSON");
			}

			document = parsed;
		}
		catch (JsonException exception)
		{
			return Failure.Invalid($"Не удалось разобрать настройки панели: {exception.Message}");
		}

		var comparer = chosen.Layout == PlatformLayout.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		var former = (document["former_instances_roots"] as JsonArray ?? [])
			.Select(item => item is JsonValue value && value.TryGetValue<string>(out var path) ? path : null)
			.OfType<string>()
			.Where(path => path.Length > 0 && !Same(chosen, path))
			.ToList();
		if (previous.Length > 0 && !Same(chosen, previous) && !former.Contains(previous, comparer))
		{
			former.Add(previous);
		}

		document["instances_root"] = chosen.Path;
		document["former_instances_roots"] = new JsonArray([.. former.Select(path => (JsonNode?) path)]);
		return document.ToJsonString(configJson) + "\n";
	}

	private static bool Same(ServersRoot chosen, string path) =>
		chosen.Is(ServersRoot.FromConfiguration(path, chosen.Layout));

	public Task<Result<PanelRestartPayload>> RestartPanelAsync(CancellationToken ct) => platform.RestartPanelAsync(ct);
}
