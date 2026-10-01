using System.Net;
using System.Text.Json.Nodes;
using Panel.Configuration.Ports;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Где слушает игра: <c>Ip</c> и <c>Port</c> из <c>serverconfig.json</c>. Файл, а не реестр:
/// игра читает именно его, и ручная правка порта видна сразу.
/// </summary>
public sealed record GameEndpoint(IPAddress? Address, int Port)
{
	/// <summary>Порт игры, если в файле его нет.</summary>
	public const int DefaultPort = 42420;

	/// <summary><c>null</c>, если файл не прочитан: судить о порте тогда не по чему.</summary>
	public static async Task<GameEndpoint?> ReadAsync(IServerConfigStore configs, InstanceId instance, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(configs);

		var file = await configs.ReadAsync(instance, ct).ConfigureAwait(false);
		if (!file.IsSuccess)
		{
			return null;
		}

		var config = file.Value.Config;
		var port = config["Port"] is JsonValue value && value.TryGetValue<int>(out var number) && number is > 0 and <= 65535
			? number
			: DefaultPort;

		// Пустой или нечитаемый адрес — все адреса, как у самой игры.
		var address = config["Ip"] is JsonValue ip && ip.TryGetValue<string>(out var text) && IPAddress.TryParse(text, out var parsed)
			? parsed
			: null;

		return new GameEndpoint(address, port);
	}
}
