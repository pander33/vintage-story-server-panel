using System.Text.Json;
using Panel.Agent.Configuration;
using Panel.Agent.Provisioning;
using Panel.Configuration.Domain;
using Panel.Contracts;
using Panel.Contracts.Agent;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Transport;

/// <summary>
/// Операции над <c>serverconfig.json</c>: чтение, запись, перечень копий, откат.
/// </summary>
/// <remarks>
/// Запись и откат следят за <c>Port</c>: порт другого сервера реестра — отказ до замены файла,
/// сменившийся порт переносится в реестр и в брандмауэр (<see cref="GamePorts"/>).
/// </remarks>
public sealed class ConfigOperations : IServerOperations
{
	private readonly ConfigBackend config;
	private readonly GamePorts ports;

	public ConfigOperations(ConfigBackend config, GamePorts ports)
	{
		ArgumentNullException.ThrowIfNull(config);
		ArgumentNullException.ThrowIfNull(ports);
		this.config = config;
		this.ports = ports;
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
		if (!staged.IsSuccess)
		{
			return Refuse(staged.Failure!);
		}

		var before = await GamePorts.ConfiguredPortAsync(server, ct).ConfigureAwait(false);
		var taken = await ports.TakenAsync(server.Id, ct).ConfigureAwait(false);
		var written = await config.WriteAsync(server, staged.Value, document => GamePorts.Clash(document, taken), ct).ConfigureAwait(false);
		if (!written.IsSuccess)
		{
			return Refuse(written.Failure!);
		}

		var warning = await MoveAsync(server, before, ct).ConfigureAwait(false);
		return Reply(Result.Ok(written.Value with { FirewallWarning = warning }));
	}

	private async Task<AgentReply> RollbackAsync(AgentRequest request, RegisteredServer server, CancellationToken ct)
	{
		var name = ConfigBackupName.Create(Argument(request, 0));
		if (!name.IsSuccess)
		{
			return Refuse(name.Failure!);
		}

		var before = await GamePorts.ConfiguredPortAsync(server, ct).ConfigureAwait(false);
		var taken = await ports.TakenAsync(server.Id, ct).ConfigureAwait(false);
		var restored = await config.RollbackAsync(server, name.Value, document => GamePorts.Clash(document, taken), ct).ConfigureAwait(false);
		if (!restored.IsSuccess)
		{
			return Refuse(restored.Failure!);
		}

		var warning = await MoveAsync(server, before, ct).ConfigureAwait(false);
		return Reply(Result.Ok(restored.Value with { FirewallWarning = warning }));
	}

	/// <summary>Порт записанного файла против прежнего: перенос в реестр и брандмауэр.</summary>
	private async Task<Phrase?> MoveAsync(RegisteredServer server, int before, CancellationToken ct)
	{
		var after = await GamePorts.ConfiguredPortAsync(server, ct).ConfigureAwait(false);
		return await ports.MoveAsync(server.Id, before, after, ct).ConfigureAwait(false);
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
		failure.Text);
}
