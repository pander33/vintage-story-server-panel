using System.Globalization;
using Panel.Contracts.Agent;
using Panel.Runtime.Domain;
using Panel.Runtime.Ports;
using Panel.SharedKernel;

namespace Panel.Agent.Client;

/// <summary>
/// Порт Runtime через привилегированный демон. Адаптер только переводит контракт в доменные
/// типы и ничего не решает.
/// </summary>
public sealed class AgentServerRuntime : IServerRuntime
{
	private readonly AgentClient client;

	public AgentServerRuntime(AgentClient client)
	{
		ArgumentNullException.ThrowIfNull(client);
		this.client = client;
	}

	public string Name => "systemd";

	public async Task<Result<ServiceSnapshot>> StatusAsync(InstanceId instance, CancellationToken ct)
	{
		var payload = await client
			.CallAsync<ServiceStatusPayload>(AgentProtocol.OpStatus, instance, [], ct)
			.ConfigureAwait(false);

		return payload.Map(ToSnapshot);
	}

	public async Task<Result<Done>> ServiceAsync(
		InstanceId instance, ServiceAction action, TimeSpan timeout, CancellationToken ct)
	{
		var result = await client
			.CallAsync<AcknowledgedPayload>(AgentProtocol.OpService, instance, [Wire(action)], ct)
			.ConfigureAwait(false);

		return result.Map(_ => Done.Value);
	}

	public async Task<Result<IReadOnlyList<LogEntry>>> LogsAsync(InstanceId instance, int count, CancellationToken ct)
	{
		var page = await client
			.CallAsync<LogPagePayload>(
				AgentProtocol.OpLogs, instance, [count.ToString(CultureInfo.InvariantCulture)], ct)
			.ConfigureAwait(false);

		return page.Map(value => (IReadOnlyList<LogEntry>) [.. value.Entries.Select(ToEntry)]);
	}

	public async Task<Result<LogPage>> LogsFromAsync(InstanceId instance, LogCursor cursor, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(cursor);

		var page = await client
			.CallAsync<LogPagePayload>(AgentProtocol.OpLogsFrom, instance, [cursor.Serialize()], ct)
			.ConfigureAwait(false);

		return page.Map(value => new LogPage(
			[.. value.Entries.Select(ToEntry)],

			// Курсор возвращается без изменений; разбор нужен, только чтобы в домен не попала
			// произвольная строка.
			LogCursor.Parse(value.Cursor, PlatformLayout.Linux) is { IsSuccess: true } parsed
				? parsed.Value
				: new LogCursor.Tail()));
	}

	public async Task<Result<ConsoleEcho>> SendAsync(InstanceId instance, ConsoleDispatch dispatch, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(dispatch);

		// Демону уходит действие и значение, а не готовая строка: строку он собирает сам по своей
		// копии каталога: веб-процесс не может прислать команду в обход списка разрешённых.
		// Произвольная строка идёт отдельной операцией со своей проверкой.
		var echo = dispatch.IsRaw
			? await client
				.CallAsync<ConsoleEchoPayload>(AgentProtocol.OpConsoleRaw, instance, [dispatch.Line], ct)
				.ConfigureAwait(false)
			: await client
				.CallAsync<ConsoleEchoPayload>(
					AgentProtocol.OpConsole, instance, [dispatch.Action, dispatch.Value], ct)
				.ConfigureAwait(false);

		return echo.Map(value => new ConsoleEcho(value.Lines, TimeSpan.FromMilliseconds(value.WaitedMilliseconds)));
	}

	public async Task<Result<ConsoleCapability>> CapabilitiesAsync(InstanceId instance, CancellationToken ct)
	{
		var payload = await client
			.CallAsync<ConsoleCapabilityPayload>(AgentProtocol.OpCapabilities, instance, [], ct)
			.ConfigureAwait(false);

		return payload.Map(value => new ConsoleCapability(
			ConsoleCapability.ParseTransport(value.Transport),
			value.Available,
			value.Reason));
	}

	internal static string Wire(ServiceAction action) => action switch
	{
		ServiceAction.Start => "start",
		ServiceAction.Stop => "stop",
		_ => "restart",
	};

	internal static LogEntry ToEntry(LogEntryPayload payload) => new(
		UnixTime.FromSeconds(payload.Timestamp),
		payload.Message,
		payload.Priority,
		payload.Source,

		// Старые версии демона не присылают строку; тогда берётся сообщение.
		payload.Line.Length > 0 ? payload.Line : payload.Message);

	internal static ServiceSnapshot ToSnapshot(ServiceStatusPayload payload)
	{
		var identity = ProcessIdentity.Parse(payload.Instance);

		return new ServiceSnapshot
		{
			IsActive = payload.Active,
			State = ServiceSnapshot.ParseState(payload.State),
			StateText = payload.State,
			SubState = payload.SubState,

			// Нераспознанная идентичность даёт Unknown, а не отказ: процесс мог завершиться между
			// systemctl show и чтением /proc, а статус всё равно нужен.
			Identity = identity.IsSuccess ? identity.Value : ProcessIdentity.Unknown,
			Pid = payload.Pid,
			StartedAt = UnixTime.FromSecondsOrNull(payload.StartedAt),
			Uptime = TimeSpan.FromSeconds(payload.Uptime),
			MemoryBytes = payload.Memory,
			CpuNanoseconds = payload.CpuNs,
			RestartCount = payload.Restarts,
			StartedText = payload.Started,
			Unit = payload.Unit,
		};
	}
}
