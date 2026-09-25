using System.Text.Json;
using Panel.Agent.Configuration;
using Panel.Configuration.Domain;
using Panel.Contracts;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Transport;

/// <summary>
/// Операции над <c>serverconfig.json</c>: чтение, запись, перечень копий, откат.
/// </summary>
public sealed class ConfigOperations : IServerOperations
{
	private readonly ConfigBackend config;

	public ConfigOperations(ConfigBackend config)
	{
		ArgumentNullException.ThrowIfNull(config);
		this.config = config;
	}

	public static bool Handles(string op) => op is
		AgentProtocol.OpConfigGet or AgentProtocol.OpConfigPut
		or AgentProtocol.OpConfigBackups or AgentProtocol.OpConfigRollback;

	bool IServerOperations.Serves(string op) => Handles(op);

	public async Task<AgentReply> ExecuteAsync(AgentRequest request, RegisteredServer server, CancellationToken ct) =>
		request.Op switch
		{
			AgentProtocol.OpConfigGet => Reply(await config.ReadAsync(server, ct).ConfigureAwait(false)),
			AgentProtocol.OpConfigBackups => Reply(config.ListBackups(server)),
			AgentProtocol.OpConfigPut => await WriteAsync(request, server, ct).ConfigureAwait(false),
			AgentProtocol.OpConfigRollback => await RollbackAsync(request, server, ct).ConfigureAwait(false),
			_ => AgentReply.Failure("op", $"Неизвестная операция: {request.Op}"),
		};

	private async Task<AgentReply> WriteAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
	{
		var staged = StagedFile.Create(Argument(request, 0));

		return staged.IsSuccess
			? Reply(await config.WriteAsync(server, staged.Value, ct).ConfigureAwait(false))
			: Refuse(staged.Failure!);
	}

	private async Task<AgentReply> RollbackAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
	{
		var name = ConfigBackupName.Create(Argument(request, 0));

		return name.IsSuccess
			? Reply(await config.RollbackAsync(server, name.Value, ct).ConfigureAwait(false))
			: Refuse(name.Failure!);
	}

	private static string? Argument(AgentRequest request, int index) =>
		index < request.Args.Count ? request.Args[index] : null;

	private static AgentReply Reply<T>(Result<T> result) =>
		result.Match(
			value => AgentReply.Success(JsonSerializer.SerializeToNode(value, PanelJson.Options)),
			Refuse);

	private static AgentReply Refuse(Failure failure) => AgentReply.Failure(
		failure.Code switch
		{
			FailureCode.NotFound => "notfound",
			FailureCode.Invalid => "invalid",
			FailureCode.Conflict => "conflict",
			_ => "unavailable",
		},
		failure.Message);
}
