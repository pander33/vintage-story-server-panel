using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Fleet.Domain;
using Panel.Fleet.Ports;
using Panel.SharedKernel;

namespace Panel.Fleet.Infrastructure;

/// <summary>
/// Реестр на диске: <c>/etc/vs-panel/instances.json</c> для версии 2, плоский
/// <c>/etc/vs-panel.json</c> для версии 1.
/// </summary>
/// <remarks>
/// Нечитаемый реестр даёт <see cref="RegistrySnapshot.Unavailable"/>, а не пустой список.
/// Панель должна запускаться без реестра, но не должна при этом считать, что серверов нет.
/// </remarks>
public sealed class FileRegistryStore : IRegistryStore
{
	private readonly string panelConfigPath;

	public FileRegistryStore(string panelConfigPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(panelConfigPath);
		this.panelConfigPath = panelConfigPath;
	}

	public async Task<RegistrySnapshot> LoadAsync(CancellationToken ct)
	{
		string panelJson;
		try
		{
			panelJson = await File.ReadAllTextAsync(panelConfigPath, ct).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return new RegistrySnapshot.Unavailable(Phrase.Format($"Не читается {panelConfigPath}: {exception.Message}"));
		}

		var configuration = PanelConfiguration.Parse(panelJson);
		if (!configuration.IsSuccess)
		{
			return new RegistrySnapshot.Unavailable(configuration.Failure!.Text);
		}

		return configuration.Value.Version < 2
			? Flat(configuration.Value)
			: await RegistryFileAsync(configuration.Value.InstancesFile, ct).ConfigureAwait(false);
	}

	private static RegistrySnapshot Flat(PanelConfiguration configuration)
	{
		var entry = configuration.FlatInstance;
		if (entry is null)
		{
			return new RegistrySnapshot.Unavailable(Phrase.From("Плоские настройки панели пусты"));
		}

		var id = InstanceId.Parse("main");
		return new RegistrySnapshot.Available([Map(id, entry)], id, RegistryStamp.Absent);
	}

	private static async Task<RegistrySnapshot> RegistryFileAsync(string path, CancellationToken ct)
	{
		RegistryStamp stamp;
		string json;
		try
		{
			var info = new FileInfo(path);
			stamp = info.Exists
				? new RegistryStamp(info.Length, info.LastWriteTimeUtc)
				: RegistryStamp.Absent;
			json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
		}
		catch (FileNotFoundException)
		{
			// Реестра ещё нет: так выглядит машина до мастера установки. Это пустой реестр, а не
			// недоступный.
			return new RegistrySnapshot.Available([], null, RegistryStamp.Absent);
		}
		catch (DirectoryNotFoundException)
		{
			return new RegistrySnapshot.Available([], null, RegistryStamp.Absent);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return new RegistrySnapshot.Unavailable(Phrase.Format($"Не читается {path}: {exception.Message}"));
		}

		JsonNode? node;
		try
		{
			node = JsonNode.Parse(json);
		}
		catch (JsonException exception)
		{
			return new RegistrySnapshot.Unavailable(Phrase.Format($"Реестр повреждён: {exception.Message}"));
		}

		if (node is not JsonObject root || root["instances"] is not JsonObject instances)
		{
			return new RegistrySnapshot.Unavailable(Phrase.From("Реестр не содержит раздела instances"));
		}

		var servers = new List<RegisteredServer>();
		foreach (var (key, value) in instances)
		{
			var id = InstanceId.Create(key);
			if (!id.IsSuccess || value is not JsonObject entry)
			{
				// Испорченная запись пропускается, чтобы один неверный идентификатор не скрыл остальные
				// серверы.
				continue;
			}

			servers.Add(Map(id.Value, entry));
		}

		servers.Sort(static (left, right) => string.CompareOrdinal(left.Id.Value, right.Id.Value));

		var declared = InstanceId.Create((string?) root["default"]);
		return new RegistrySnapshot.Available(
			servers,
			declared.IsSuccess ? declared.Value : null,
			stamp);
	}

	private static RegisteredServer Map(InstanceId id, JsonObject entry)
	{
		var data = (string?) entry["data_dir"] ?? string.Empty;

		return new RegisteredServer
		{
			Id = id,
			Name = (string?) entry["name"] is { Length: > 0 } name ? name : id.Value,
			Unit = (string?) entry["unit"] ?? string.Empty,
			DataDirectory = data,
			ProgramDirectory = (string?) entry["program_dir"] ?? string.Empty,
			ModsDirectory = (string?) entry["mods_dir"] ?? string.Empty,
			BackupDirectory = (string?) entry["backup_dir"] ?? string.Empty,
			ConfigFile = (string?) entry["config_file"] ?? string.Empty,
			// Выводимые каталоги: реестр может указать их явно, иначе они там же, где их создаёт
			// instance-create.
			WorldDirectory = Derived(entry, "world_dir", data, "Saves"),
			LogDirectory = Derived(entry, "logs_dir", data, "Logs"),
			ModConfigDirectory = Derived(entry, "mod_config_dir", data, "ModConfig"),
			TelemetryFile = (string?) entry["telemetry_file"] ?? Join(data, "VsPanel", "telemetry.json"),
			GameEventsFile = (string?) entry["game_events_file"] ?? Join(data, "VsPanel", "events.jsonl"),
			CommandTransport = (string?) entry["command_transport"] ?? "disabled",
			ConsoleFifo = (string?) entry["console_fifo"] ?? string.Empty,
			ScreenSession = (string?) entry["screen_session"] ?? string.Empty,
			CommandRunner = (string?) entry["command_runner"] ?? string.Empty,
			GameVersion = (string?) entry["game_version"],
			Raw = entry,
		};
	}

	private static string Derived(JsonObject entry, string key, string data, string child) =>
		(string?) entry[key] is { Length: > 0 } declared ? declared : Join(data, child);

	/// <summary>
	/// Склейка пути. Разделитель берётся по каталогу данных, а не по текущей машине: реестр с
	/// <c>layout: windows</c> может читаться и здесь.
	/// </summary>
	private static string Join(string root, params string[] parts)
	{
		if (root.Length == 0)
		{
			return string.Empty;
		}

		var separator = root.Contains('\\') ? '\\' : '/';
		return root.TrimEnd('/', '\\') + separator + string.Join(separator, parts);
	}
}
