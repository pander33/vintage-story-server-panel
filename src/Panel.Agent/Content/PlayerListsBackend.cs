using Panel.Fleet.Domain;
using Panel.Runtime.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Content;

/// <summary>
/// Списки забаненных игроков и белого списка на диске: <c>Playerdata/playersbanned.json</c> и
/// <c>Playerdata/playerswhitelisted.json</c> (интерфейс 2.1 §3).
/// </summary>
/// <remarks>
/// Точный набор полей в этих файлах не проверен на настоящем сервере (открытый вопрос плана);
/// разбор — в <see cref="PlayerLists.ParseEntries"/>, здесь только безопасное чтение файла: предел
/// размера и отказ на символической ссылке, как у <c>TelemetryFileReader</c>.
/// </remarks>
public sealed class PlayerListsBackend
{
	private const long maxBytes = 1024 * 1024;

	public async Task<Result<PlayerLists>> ReadAsync(RegisteredServer server, CancellationToken ct)
	{
		if (server.DataDirectory.Length == 0)
		{
			return Failure.Unavailable("Каталог данных не задан в реестре");
		}

		var directory = Path.Combine(server.DataDirectory, "Playerdata");
		var banned = await ReadFileAsync(Path.Combine(directory, "playersbanned.json"), ct).ConfigureAwait(false);
		if (!banned.IsSuccess)
		{
			return Result.Fail<PlayerLists>(banned.Failure!);
		}

		var whitelisted = await ReadFileAsync(Path.Combine(directory, "playerswhitelisted.json"), ct).ConfigureAwait(false);
		if (!whitelisted.IsSuccess)
		{
			return Result.Fail<PlayerLists>(whitelisted.Failure!);
		}

		try
		{
			return new PlayerLists(PlayerLists.ParseEntries(banned.Value), PlayerLists.ParseEntries(whitelisted.Value));
		}
		catch (System.Text.Json.JsonException exception)
		{
			return Failure.Invalid($"Не удалось разобрать список игроков: {exception.Message}");
		}
	}

	/// <summary>Отсутствующий файл — пустой список, а не отказ; так игра ведёт себя без единого бана.</summary>
	private static async Task<Result<string>> ReadFileAsync(string path, CancellationToken ct)
	{
		FileInfo info;
		try
		{
			info = new FileInfo(path);
			if (!info.Exists)
			{
				return string.Empty;
			}

			if (info.LinkTarget is not null || info.Length > maxBytes)
			{
				return Failure.Invalid($"Файл списка игроков недопустим: {Path.GetFileName(path)}");
			}

			return await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Не удалось прочитать {Path.GetFileName(path)}: {exception.Message}");
		}
	}
}
