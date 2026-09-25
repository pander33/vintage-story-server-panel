using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Panel.Agent.Runtime;
using Panel.Contracts;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.Fleet.Infrastructure;
using Panel.Fleet.Ports;
using Panel.SharedKernel;

namespace Panel.Agent.Transport;

/// <summary>
/// Операции над реестром, адресованные серверу <c>-</c>: перечень, переименование, «забыть».
/// </summary>
/// <remarks>
/// Удаление сервера целиком находится в <see cref="ProvisionOperations"/> рядом с созданием:
/// оно использует те же выведенные пути и удаляет юнит systemd.
/// </remarks>
public sealed partial class RegistryOperations : IRegistryOperations
{
	private readonly IRegistryStore registry;
	private readonly RegistryEditor editor;
	private readonly IServiceBackend service;

	public RegistryOperations(IRegistryStore registry, RegistryEditor editor, IServiceBackend service)
	{
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(editor);
		ArgumentNullException.ThrowIfNull(service);

		this.registry = registry;
		this.editor = editor;
		this.service = service;
	}

	public static bool Handles(string op) => op is
		AgentProtocol.OpInstances or AgentProtocol.OpInstanceRename or AgentProtocol.OpInstanceForget;

	bool IRegistryOperations.Serves(string op) => Handles(op);

	public async Task<AgentReply> ExecuteAsync(AgentRequest request, CancellationToken ct) =>
		request.Op switch
		{
			AgentProtocol.OpInstances => await ListAsync(ct).ConfigureAwait(false),
			AgentProtocol.OpInstanceRename => await RenameAsync(request, ct).ConfigureAwait(false),
			AgentProtocol.OpInstanceForget => await ForgetAsync(request, ct).ConfigureAwait(false),
			_ => AgentReply.Failure("op", $"Неизвестная операция: {request.Op}"),
		};

	/// <summary>
	/// Перечень со сведениями о каждом сервере с диска: порт и адрес из <c>serverconfig.json</c>,
	/// версия из <c>server-main.log</c>. Для серверов из установок старше панели этого нет в
	/// реестре.
	/// </summary>
	private async Task<AgentReply> ListAsync(CancellationToken ct)
	{
		var snapshot = await registry.LoadAsync(ct).ConfigureAwait(false);
		if (snapshot is not RegistrySnapshot.Available available)
		{
			return AgentReply.Failure("unavailable", ((RegistrySnapshot.Unavailable) snapshot).Detail);
		}

		var items = new List<RegistryEntryPayload>();
		foreach (var server in available.Servers)
		{
			var facts = await ObservedAsync(server, ct).ConfigureAwait(false);
			var port = server.Raw?["port"] is JsonValue declared && declared.TryGetValue<int>(out var number) ? number : 0;

			items.Add(new RegistryEntryPayload
			{
				Id = server.Id.Value,
				Name = server.Name,
				Default = server.Id == available.Default,
				GameVersion = server.GameVersion is { Length: > 0 } version ? version : facts.GameVersion ?? string.Empty,
				Port = port != 0 ? port : facts.Port,
				Ip = facts.Ip ?? string.Empty,
				Unit = server.Unit,
				DataDirectory = server.DataDirectory,
				ProgramDirectory = (string?) server.Raw?["program_dir"] ?? string.Empty,
				Active = await service.IsActiveAsync(server, ct).ConfigureAwait(false),
			});
		}

		return Ok(new RegistryListingPayload
		{
			Instances = items,
			Default = available.Default?.Value,
			Writable = await editor.IsWritableAsync(ct).ConfigureAwait(false),
		});
	}

	private async Task<AgentReply> RenameAsync(AgentRequest request, CancellationToken ct)
	{
		var id = InstanceId.Create(Argument(request, 0));
		var name = InstanceName.Create(Argument(request, 1));
		if (!id.IsSuccess || !name.IsSuccess)
		{
			return AgentReply.Failure("invalid", (id.Failure ?? name.Failure)!.Message);
		}

		var renamed = await editor.EditAsync(document =>
		{
			if (document.Instances[id.Value.Value] is not JsonObject entry)
			{
				return Failure.NotFound($"Неизвестный сервер {id.Value}");
			}

			entry["name"] = name.Value.Value;
			return Result.Ok(new InstanceRenamedPayload { Id = id.Value.Value, Name = name.Value.Value });
		}, ct).ConfigureAwait(false);

		return Reply(renamed);
	}

	/// <summary>
	/// Убрать сервер из реестра, не трогая его файлов. Работающий сервер убрать нельзя: панель
	/// потеряла бы контроль над процессом.
	/// </summary>
	private async Task<AgentReply> ForgetAsync(AgentRequest request, CancellationToken ct)
	{
		var id = InstanceId.Create(Argument(request, 0));
		if (!id.IsSuccess || id.Value.IsRegistry)
		{
			return AgentReply.Failure("invalid", "Недопустимый идентификатор сервера");
		}

		var snapshot = await registry.LoadAsync(ct).ConfigureAwait(false);
		var server = (snapshot as RegistrySnapshot.Available)?.Find(id.Value);
		if (server is not null && await service.IsActiveAsync(server, ct).ConfigureAwait(false))
		{
			return AgentReply.Failure("conflict", "Остановите сервер, прежде чем убрать его из реестра");
		}

		var forgotten = await editor.EditAsync(document =>
		{
			var registered = document.Registered;
			var allowed = RegistryRules.CanRemove(registered, id.Value);
			if (!allowed.IsSuccess)
			{
				return Result.Fail<InstanceForgottenPayload>(allowed.Failure!);
			}

			var entry = document.Instances[id.Value.Value] as JsonObject;
			var next = RegistryRules.DefaultAfterRemoving(registered, document.Default, id.Value);

			document.Instances.Remove(id.Value.Value);
			document.Default = next;

			return Result.Ok(new InstanceForgottenPayload
			{
				Id = id.Value.Value,
				Default = next.Value,
				DataDirectory = server?.DataDirectory ?? (string?) entry?["data_dir"] ?? string.Empty,
				ProgramDirectory = (string?) entry?["program_dir"] ?? string.Empty,
			});
		}, ct).ConfigureAwait(false);

		return Reply(forgotten);
	}

	private static async Task<(int Port, string? Ip, string? GameVersion)> ObservedAsync(
		RegisteredServer server,
		CancellationToken ct)
	{
		var port = 0;
		string? ip = null;
		string? version = null;

		try
		{
			if (JsonNode.Parse(await File.ReadAllTextAsync(server.ConfigFile, ct).ConfigureAwait(false)) is JsonObject config)
			{
				if (config["Port"] is JsonValue declared && declared.TryGetValue<int>(out var number) && number is > 0 and < 65536)
				{
					port = number;
				}

				if ((string?) config["Ip"] is { } address && address.Trim().Length > 0)
				{
					var trimmed = address.Trim();
					ip = trimmed.Length > 64 ? trimmed[..64] : trimmed;
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
		{
			// Нечитаемые данные просто не попадают в сведения.
		}

		try
		{
			var log = Path.Combine(server.LogDirectory, "server-main.log");
			if (File.Exists(log))
			{
				var buffer = new byte[256 * 1024];
				int read;
				await using (var file = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
				{
					read = await file.ReadAsync(buffer, ct).ConfigureAwait(false);
				}

				var match = GameVersionInLog().Match(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
				version = match.Success ? match.Groups[1].Value : null;
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Аналогично.
		}

		return (port, ip, version);
	}

	private static string? Argument(AgentRequest request, int index) =>
		index < request.Args.Count ? request.Args[index] : null;

	private static AgentReply Ok<T>(T value) =>
		AgentReply.Success(JsonSerializer.SerializeToNode(value, PanelJson.Options));

	private static AgentReply Reply<T>(Result<T> result) =>
		result.Match(
			Ok,
			failure => AgentReply.Failure(
				failure.Code switch
				{
					FailureCode.NotFound => "notfound",
					FailureCode.Invalid => "invalid",
					FailureCode.Conflict => "conflict",
					_ => "unavailable",
				},
				failure.Message));

	[GeneratedRegex(@"Game Version: v(\d{1,3}\.\d{1,3}\.\d{1,4}(?:-[A-Za-z0-9.]{1,16})?)")]
	private static partial Regex GameVersionInLog();
}
