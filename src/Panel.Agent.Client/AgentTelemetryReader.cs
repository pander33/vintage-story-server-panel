using Panel.Contracts.Agent;
using Panel.Runtime.Domain;
using Panel.Runtime.Ports;
using Panel.SharedKernel;

namespace Panel.Agent.Client;

/// <summary>
/// Чтение телеметрии через демон: файл лежит в игровом каталоге, к которому у веб-процесса
/// нет доступа.
/// </summary>
public sealed class AgentTelemetryReader : ITelemetryReader
{
	private readonly AgentClient client;

	public AgentTelemetryReader(AgentClient client)
	{
		ArgumentNullException.ThrowIfNull(client);
		this.client = client;
	}

	public async Task<TelemetryReading> ReadAsync(InstanceId instance, CancellationToken ct)
	{
		var payload = await client
			.CallAsync<TelemetryPayload>(AgentProtocol.OpTelemetry, instance, [], ct)
			.ConfigureAwait(false);

		return payload.Match(ToReading, failure => new TelemetryReading.Unavailable(failure.Text));
	}

	internal static TelemetryReading ToReading(TelemetryPayload payload)
	{
		if (!payload.Available)
		{
			return new TelemetryReading.Unavailable(
				payload.Detail is null || string.IsNullOrWhiteSpace(payload.Detail.ToString()) ? Phrase.From("Телеметрия недоступна") : payload.Detail);
		}

		var snapshot = new TelemetrySnapshot
		{
			GeneratedAt = UnixTime.FromSecondsOrNull(payload.GeneratedAt),
			TicksPerSecond = payload.TicksPerSecond,
			Players = [.. payload.Players.Select(player =>
				new PlayerSample(player.Name, player.Uid, player.Ping, player.Address))],
		};

		return payload.Stale
			? new TelemetryReading.Stale(snapshot, TimeSpan.FromSeconds(payload.AgeSeconds ?? 0))
			: new TelemetryReading.Fresh(snapshot);
	}
}
