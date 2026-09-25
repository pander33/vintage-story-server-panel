using Panel.Content.Domain;
using Panel.Fleet.Infrastructure;
using Panel.SharedKernel;

namespace Panel.Agent.Content;

/// <summary>
/// Каталог, через который веб-процесс передаёт демону крупные файлы.
/// </summary>
/// <remarks>
/// <para>
/// Мод весит десятки мегабайт, мир — гигабайты, по сокету их не передать. Веб-процесс пишет
/// файл сюда, демон забирает его по имени и сам строит путь, как и для игровых каталогов.
/// </para>
/// <para>
/// Проверки: файл лежит прямо в этом каталоге, это обычный файл с одной жёсткой ссылкой,
/// расширение соответствует назначению. Владелец не проверяется: подключившийся процесс
/// проверяет <c>SO_PEERCRED</c> на соединении.
/// </para>
/// </remarks>
public sealed class StagingArea
{
	private readonly string panelConfigPath;

	public StagingArea(string panelConfigPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(panelConfigPath);
		this.panelConfigPath = panelConfigPath;
	}

	/// <summary>Каталог промежуточного хранения из настроек панели.</summary>
	public async Task<Result<string>> DirectoryAsync(CancellationToken ct)
	{
		string json;
		try
		{
			json = await File.ReadAllTextAsync(panelConfigPath, ct).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Не читается {panelConfigPath}: {exception.Message}");
		}

		var configuration = PanelConfiguration.Parse(json);
		if (!configuration.IsSuccess)
		{
			return Result.Fail<string>(configuration.Failure!);
		}

		return configuration.Value.StagingDirectory is { Length: > 0 } directory
			? directory
			: Failure.Unavailable("Каталог промежуточного хранения не задан");
	}

	/// <summary>Путь к загруженному файлу, если файл на месте и соответствует заявленному.</summary>
	public async Task<Result<string>> ResolveAsync(StagedFile staged, string extension, CancellationToken ct)
	{
		var directory = await DirectoryAsync(ct).ConfigureAwait(false);
		if (!directory.IsSuccess)
		{
			return directory;
		}

		var path = Path.Combine(directory.Value, staged.Name);

		if (!staged.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
		{
			return Failure.Invalid($"Ожидался файл {extension}");
		}

		var info = new FileInfo(path);
		if (info is not { Exists: true, LinkTarget: null })
		{
			return Failure.NotFound("Загруженный файл не найден");
		}

		// Имя уже проверено, но полный путь сверяется ещё раз: ошибка здесь означала бы запись
		// от root в произвольное место.
		var resolved = Path.GetFullPath(path);
		if (Path.GetDirectoryName(resolved) != Path.GetFullPath(directory.Value))
		{
			return Failure.Invalid("Загруженный файл лежит вне разрешённого каталога");
		}

		return resolved;
	}

	/// <summary>Путь, по которому демон положит файл для скачивания панелью.</summary>
	public async Task<Result<string>> ReserveAsync(StagedFile staged, CancellationToken ct)
	{
		var directory = await DirectoryAsync(ct).ConfigureAwait(false);
		if (!directory.IsSuccess)
		{
			return directory;
		}

		Directory.CreateDirectory(directory.Value);
		var path = Path.Combine(directory.Value, staged.Name);

		return File.Exists(path)
			? Failure.Conflict("Временный файл с таким именем уже существует")
			: path;
	}
}
