using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Agent.Provisioning;
using Panel.Contracts;
using Panel.Contracts.Agent;
using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Transport;

/// <summary>
/// Создание сервера и дела самой панели: операции, адресованные серверу <c>-</c>.
/// </summary>
/// <remarks>
/// Число аргументов каждой операции закреплено. Описание сервера приходит в первом аргументе
/// JSON-текстом, а не файлом в staging, чтобы не было ещё одного пути для проверки. Описание
/// проверяет <see cref="InstanceSpecification"/>.
/// </remarks>
public sealed class ProvisionOperations : IRegistryOperations
{
	private static readonly Dictionary<string, int> arity = new(StringComparer.Ordinal)
	{
		[AgentProtocol.OpRuntimeProbe] = 0,
		[AgentProtocol.OpDiskFree] = 0,
		[AgentProtocol.OpInstanceCreate] = 1,
		[AgentProtocol.OpInstanceServiceInstall] = 1,
		[AgentProtocol.OpPanelEnvSet] = 2,
		[AgentProtocol.OpPanelRestart] = 0,
		[AgentProtocol.OpInstanceDelete] = 1,
		[AgentProtocol.OpPanelLogs] = 1,
		[AgentProtocol.OpInstancesRootSet] = 1,
	};

	private readonly ProvisionBackend backend;

	public ProvisionOperations(ProvisionBackend backend)
	{
		ArgumentNullException.ThrowIfNull(backend);
		this.backend = backend;
	}

	public static bool Handles(string op) => arity.ContainsKey(op);

	bool IRegistryOperations.Serves(string op) => Handles(op);

	public async Task<AgentReply> ExecuteAsync(AgentRequest request, CancellationToken ct)
	{
		if (!arity.TryGetValue(request.Op, out var expected))
		{
			return AgentReply.Failure("op", $"Неизвестная операция: {request.Op}");
		}

		if (request.Args.Count != expected)
		{
			return AgentReply.Failure("invalid", $"Операция {request.Op} принимает аргументов: {expected}");
		}

		try
		{
			return request.Op switch
			{
				AgentProtocol.OpRuntimeProbe => Ok(await backend.ProbeRuntimeAsync(ct).ConfigureAwait(false)),
				AgentProtocol.OpDiskFree => Reply(await backend.DiskFreeAsync(ct).ConfigureAwait(false)),
				AgentProtocol.OpInstanceCreate => Reply(await CreateAsync(request.Args[0], ct).ConfigureAwait(false)),
				AgentProtocol.OpInstanceServiceInstall => Reply(await InstallAsync(request.Args[0], ct).ConfigureAwait(false)),
				AgentProtocol.OpPanelEnvSet => Reply(await SetAsync(request.Args[0], request.Args[1], ct).ConfigureAwait(false)),
				AgentProtocol.OpInstanceDelete => Reply(await DeleteAsync(request.Args[0], ct).ConfigureAwait(false)),
				AgentProtocol.OpPanelLogs => Reply(await backend.PanelLogsAsync(request.Args[0], ct).ConfigureAwait(false)),
				AgentProtocol.OpInstancesRootSet => Reply(await backend.SetInstancesRootAsync(request.Args[0], ct).ConfigureAwait(false)),
				_ => Reply(await backend.RestartPanelAsync(ct).ConfigureAwait(false)),
			};
		}
		catch (TimeoutException exception)
		{
			return AgentReply.Failure("timeout", exception.Message);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return AgentReply.Failure("io", exception.Message);
		}
	}

	private Task<Result<InstanceCreatedPayload>> CreateAsync(string text, CancellationToken ct)
	{
		JsonNode? specification;
		try
		{
			specification = JsonNode.Parse(text);
		}
		catch (JsonException exception)
		{
			return Task.FromResult<Result<InstanceCreatedPayload>>(
				Failure.Invalid($"Cannot read instance specification: {exception.Message}"));
		}

		return backend.CreateAsync(specification, ct);
	}

	private Task<Result<ServiceInstalledPayload>> InstallAsync(string text, CancellationToken ct)
	{
		var id = InstanceId.Create(text);
		return id.IsSuccess && !id.Value.IsRegistry
			? backend.InstallServiceAsync(id.Value, ct)
			: Task.FromResult<Result<ServiceInstalledPayload>>(Failure.Invalid("Unsafe instance id"));
	}

	private Task<Result<InstanceDeletedPayload>> DeleteAsync(string text, CancellationToken ct)
	{
		var id = InstanceId.Create(text);
		return id.IsSuccess && !id.Value.IsRegistry
			? backend.DeleteInstanceAsync(id.Value, ct)
			: Task.FromResult<Result<InstanceDeletedPayload>>(Failure.Invalid("Unsafe instance id"));
	}

	private Task<Result<PanelEnvironmentSetPayload>> SetAsync(string key, string value, CancellationToken ct)
	{
		var setting = PanelSetting.Create(key, value);
		return setting.IsSuccess
			? backend.SetPanelEnvironmentAsync(setting.Value, ct)
			: Task.FromResult<Result<PanelEnvironmentSetPayload>>(setting.Failure!);
	}

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
}
