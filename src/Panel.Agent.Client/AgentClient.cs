using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Panel.Contracts;
using Panel.Contracts.Agent;
using Panel.SharedKernel;

namespace Panel.Agent.Client;

/// <summary>
/// Клиент привилегированного демона. На каждый запрос открывается новое соединение.
/// </summary>
/// <remarks>
/// Это транспорт для адаптеров узких портов, а не «интерфейс агента» (ADR-01). Домен об этом
/// типе не знает; это проверяет архитектурный тест.
/// </remarks>
public sealed class AgentClient
{
	public const string PipePrefix = @"\\.\pipe\";

	/// <summary>Сколько ждать свободного экземпляра канала, пока демон занят другими.</summary>
	private static readonly TimeSpan pipeConnectTimeout = TimeSpan.FromSeconds(10);

	private readonly string socketPath;

	public AgentClient(string socketPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
		this.socketPath = socketPath;
	}

	public async Task<Result<T>> CallAsync<T>(
		string op, InstanceId instance, IReadOnlyList<string> args, CancellationToken ct)
		where T : class
	{
		var request = new AgentRequest { Op = op, Instance = instance.Value, Args = args };

		AgentReply? reply;
		try
		{
			reply = await ExchangeAsync(request, ct).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is SocketException or IOException or TimeoutException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Привилегированная служба недоступна: {exception.Message}");
		}

		if (reply is null)
		{
			return Failure.Unavailable("Привилегированная служба закрыла соединение без ответа");
		}

		if (!reply.Ok)
		{
			return Map(reply);
		}

		if (reply.Result is null)
		{
			return Failure.Unavailable($"Пустой ответ на операцию {op}");
		}

		var value = reply.Result.Deserialize<T>(PanelJson.Options);
		return value is not null
			? Result.Ok(value)
			: Failure.Unavailable($"Ответ на операцию {op} не разобран");
	}

	private async Task<AgentReply?> ExchangeAsync(AgentRequest request, CancellationToken ct)
	{
		await using var stream = await ConnectAsync(ct).ConfigureAwait(false);

		var payload = JsonSerializer.Serialize(request, PanelJson.Options) + "\n";
		await stream.WriteAsync(Encoding.UTF8.GetBytes(payload), ct).ConfigureAwait(false);
		await stream.FlushAsync(ct).ConfigureAwait(false);

		using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: true);
		var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);

		return line is null ? null : JsonSerializer.Deserialize<AgentReply>(line, PanelJson.Options);
	}

	/// <summary>
	/// Адрес вида <c>\\.\pipe\имя</c> означает именованный канал Windows, любой другой — путь
	/// Unix-сокета. Протокол одинаков: строка JSON туда, строка обратно.
	/// </summary>
	private async Task<Stream> ConnectAsync(CancellationToken ct)
	{
		if (socketPath.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase))
		{
			var pipe = new NamedPipeClientStream(
				".", socketPath[PipePrefix.Length..], PipeDirection.InOut, PipeOptions.Asynchronous);
			try
			{
				await pipe.ConnectAsync(pipeConnectTimeout, ct).ConfigureAwait(false);
				return pipe;
			}
			catch
			{
				await pipe.DisposeAsync().ConfigureAwait(false);
				throw;
			}
		}

		var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
		try
		{
			await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
			return new NetworkStream(socket, ownsSocket: true);
		}
		catch
		{
			socket.Dispose();
			throw;
		}
	}

	/// <summary>
	/// Отказ демона как отказ предметной области. Код «peer» не превращается в «нет прав»:
	/// это отказ в доверии процессу панели, а не пользователю.
	/// </summary>
	private static Failure Map(AgentReply reply)
	{
		var message = reply.FailureText(Phrase.From("Привилегированная служба отказала"));
		return reply.Code switch
		{
			"notfound" => Failure.NotFound(message),
			"conflict" => Failure.Conflict(message),
			"invalid" or "instance" or "op" or "request" or "args" => Failure.Invalid(message),
			_ => Failure.Unavailable(message),
		};
	}
}
