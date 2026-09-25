using System.Text;
using System.Text.Json;
using Panel.Contracts;
using Panel.Contracts.Agent;

namespace Panel.Agent.Transport;

/// <summary>
/// Один разговор с панелью: строка JSON в ответ на строку JSON, пока панель не закроет
/// соединение. Одинаков для Unix-сокета и именованного канала Windows.
/// </summary>
/// <remarks>
/// Кто подключился, слушатель проверяет до начала разговора: у сокета через
/// <c>SO_PEERCRED</c>, у канала через список доступа. Здесь проверяется только форма запроса.
/// </remarks>
public sealed class AgentConversation
{
	private readonly AgentDispatcher dispatcher;

	public AgentConversation(AgentDispatcher dispatcher)
	{
		ArgumentNullException.ThrowIfNull(dispatcher);
		this.dispatcher = dispatcher;
	}

	public async Task RunAsync(Stream stream, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(stream);

		using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: true);

		while (!ct.IsCancellationRequested)
		{
			string? line;
			try
			{
				line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
			}
			catch (IOException)
			{
				// Панель оборвала соединение посреди строки.
				return;
			}

			if (line is null)
			{
				return;
			}

			if (line.Length > AgentProtocol.MaxFrameBytes)
			{
				await WriteAsync(stream, AgentReply.Failure("frame", "Кадр слишком велик"), ct).ConfigureAwait(false);
				return;
			}

			var reply = await HandleAsync(line, ct).ConfigureAwait(false);
			if (!await WriteAsync(stream, reply, ct).ConfigureAwait(false))
			{
				return;
			}
		}
	}

	/// <summary>Отказ до начала разговора: слушатель не доверяет подключившемуся.</summary>
	public static Task RefuseAsync(Stream stream, string code, string reason, CancellationToken ct) =>
		WriteAsync(stream, AgentReply.Failure(code, reason), ct);

	private async Task<AgentReply> HandleAsync(string line, CancellationToken ct)
	{
		AgentRequest? request;
		try
		{
			request = JsonSerializer.Deserialize<AgentRequest>(line, PanelJson.Options);
		}
		catch (JsonException exception)
		{
			return AgentReply.Failure("request", $"Запрос не разобран: {exception.Message}");
		}

		if (request is null)
		{
			return AgentReply.Failure("request", "Пустой запрос");
		}

		if (request.Version != AgentProtocol.Version)
		{
			// Другая версия протокола — отказ: разные версии панели и демона на одной машине
			// означают незавершённое обновление.
			return AgentReply.Failure("version", $"Версия протокола {request.Version} не поддерживается");
		}

		return await dispatcher.DispatchAsync(request, ct).ConfigureAwait(false);
	}

	private static async Task<bool> WriteAsync(Stream stream, AgentReply reply, CancellationToken ct)
	{
		var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply, PanelJson.Options) + "\n");
		try
		{
			await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
			await stream.FlushAsync(ct).ConfigureAwait(false);
			return true;
		}
		catch (IOException)
		{
			// Панель отключилась, не дождавшись ответа.
			return false;
		}
	}
}
