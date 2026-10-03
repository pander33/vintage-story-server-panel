using System.Globalization;
using System.Text.Json.Nodes;
using Panel.Fleet.Domain;
using Panel.Fleet.Infrastructure;
using Panel.Fleet.Ports;
using Panel.SharedKernel;

namespace Panel.Agent.Provisioning;

/// <summary>
/// Игровые порты серверов: порт в реестре и правило брандмауэра следуют за <c>Port</c> в
/// <c>serverconfig.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Правило ставится, когда сервер получает службу (создание, перенос), переносится при смене
/// порта в настройках или откате к копии с другим портом и снимается при удалении сервера.
/// Правило порта, который занят другим сервером реестра, не снимается.
/// </para>
/// <para>
/// Отказ брандмауэра не отменяет записанное: настройки уже сохранены, а сервер уже создан.
/// Оператор получает текст и открывает порт сам.
/// </para>
/// </remarks>
public sealed class GamePorts
{
	private readonly IRegistryStore registry;
	private readonly RegistryEditor editor;
	private readonly IGameFirewall firewall;

	public GamePorts(IRegistryStore registry, RegistryEditor editor, IGameFirewall firewall)
	{
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(editor);
		ArgumentNullException.ThrowIfNull(firewall);
		this.registry = registry;
		this.editor = editor;
		this.firewall = firewall;
	}

	/// <summary><c>Port</c> документа настроек; 0, если его нет или он не число.</summary>
	public static int PortOf(JsonObject? config) =>
		config?["Port"] is JsonValue value && value.TryGetValue<int>(out var port) && port is > 0 and < 65536 ? port : 0;

	/// <summary>Порт сервера в реестре; 0, если он не записан.</summary>
	public static int PortOf(RegisteredServer server)
	{
		ArgumentNullException.ThrowIfNull(server);
		return server.Raw?["port"] is JsonValue value && value.TryGetValue<int>(out var port) ? port : 0;
	}

	/// <summary>Порт текущего файла настроек сервера; 0, если файл не читается.</summary>
	public static async Task<int> ConfiguredPortAsync(RegisteredServer server, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(server);
		try
		{
			return PortOf(JsonNode.Parse(await File.ReadAllTextAsync(server.ConfigFile, ct).ConfigureAwait(false)) as JsonObject);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
		{
			return 0;
		}
	}

	/// <summary>Порты других серверов реестра: новый порт не должен с ними совпасть.</summary>
	public async Task<IReadOnlyDictionary<int, string>> TakenAsync(InstanceId owner, CancellationToken ct)
	{
		var taken = new Dictionary<int, string>();
		if (await registry.LoadAsync(ct).ConfigureAwait(false) is RegistrySnapshot.Available available)
		{
			foreach (var server in available.Servers.Where(server => server.Id != owner))
			{
				if (PortOf(server) is > 0 and var port)
				{
					taken.TryAdd(port, server.Id.Value);
				}
			}
		}

		return taken;
	}

	/// <summary>Отказ, если порт документа занят другим сервером; иначе <see langword="null"/>.</summary>
	public static Failure? Clash(JsonObject config, IReadOnlyDictionary<int, string> taken)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(taken);
		var port = PortOf(config);
		return port > 0 && taken.TryGetValue(port, out var other)
			? Failure.Conflict($"Порт {port.ToString(CultureInfo.InvariantCulture)} уже занят сервером {other}")
			: null;
	}

	/// <summary>Открыть порт сервера, получившего службу.</summary>
	public Task<Phrase?> OpenAsync(int port, CancellationToken ct) => firewall.OpenAsync(port, ct);

	/// <summary>Снять правило порта удалённого сервера, если порт не занят другим.</summary>
	public async Task CloseAsync(InstanceId owner, int port, CancellationToken ct)
	{
		if (port > 0 && !(await TakenAsync(owner, ct).ConfigureAwait(false)).ContainsKey(port))
		{
			await firewall.CloseAsync(port, ct).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Порт в настройках сменился: записать его в реестр, снять прежнее правило, открыть новое.
	/// </summary>
	/// <returns>Текст для оператора, если открыть новый порт не удалось.</returns>
	public async Task<Phrase?> MoveAsync(InstanceId owner, int before, int after, CancellationToken ct)
	{
		if (before == after || after == 0)
		{
			return null;
		}

		await editor.EditAsync(document =>
		{
			if (document.Instances[owner.Value] is JsonObject entry)
			{
				entry["port"] = after;
			}

			return Done.Ok;
		}, ct).ConfigureAwait(false);

		await CloseAsync(owner, before, ct).ConfigureAwait(false);
		return await firewall.OpenAsync(after, ct).ConfigureAwait(false);
	}
}
