using System.Text.Json.Nodes;
using Panel.Configuration.Ports;
using Panel.Contracts;
using Panel.Contracts.Agent;
using Panel.Contracts.ServerCommands;
using Panel.Runtime.Ports;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Объявление игрокам (<c>/announce</c>) на языке сервера.
/// </summary>
/// <remarks>
/// Объявление читают игроки, а не тот, кто смотрит панель, и у фонового такта нет запроса, из
/// которого взять язык. Язык игроков — <c>ServerLanguage</c> из <c>serverconfig.json</c>:
/// <c>ru</c> — русский, всё остальное и нечитаемый файл — английский, как у игры по умолчанию.
/// Файл читается при каждом объявлении: их единицы за обслуживание, а язык мог смениться.
/// </remarks>
public sealed class Announcements(IPlayerCommands players, IServerConfigStore configs)
{
	public async Task<Result<PlayerActionPayload>> SendAsync(InstanceId instance, Phrase text, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(text);
		var language = await LanguageAsync(instance, ct).ConfigureAwait(false);
		var command = PlayerCommand.Create("announce", string.Empty, PhraseCatalog.Translate(text, language));
		if (!command.IsSuccess)
		{
			return command.Failure!;
		}

		return await players.SendAsync(instance, command.Value, ct).ConfigureAwait(false);
	}

	/// <summary>Текст администратора как есть: он уже написан на нужном языке.</summary>
	public async Task<Result<PlayerActionPayload>> SendTextAsync(InstanceId instance, string text, CancellationToken ct)
	{
		var command = PlayerCommand.Create("announce", string.Empty, text);
		if (!command.IsSuccess)
		{
			return command.Failure!;
		}

		return await players.SendAsync(instance, command.Value, ct).ConfigureAwait(false);
	}

	private async Task<PanelLanguage> LanguageAsync(InstanceId instance, CancellationToken ct)
	{
		var file = await configs.ReadAsync(instance, ct).ConfigureAwait(false);
		var code = file.IsSuccess && file.Value.Config["ServerLanguage"] is JsonValue value && value.TryGetValue<string>(out var text)
			? text
			: null;
		return code is not null && code.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? PanelLanguage.Russian : PanelLanguage.English;
	}
}
