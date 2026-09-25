using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Content.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Content;

/// <summary>Разобранный <c>modinfo.json</c>.</summary>
/// <param name="Id">Идентификатор мода.</param>
/// <param name="Version">Версия как её объявил автор.</param>
/// <param name="DisplayName">Название для интерфейса.</param>
/// <param name="Side">Сторона: server, client, universal.</param>
/// <param name="Dependencies">Зависимости: идентификатор — требуемая версия.</param>
public sealed record ModManifest(
	ModId Id,
	string Version,
	string DisplayName,
	string Side,
	IReadOnlyDictionary<string, string> Dependencies);

/// <summary>
/// Чтение ZIP-архива мода. Архив приходит извне, поэтому читается с пределами и только в память.
/// </summary>
public static class ModArchive
{
	/// <summary>Предел числа записей в архиве.</summary>
	private const int maxEntries = 20_000;

	/// <summary>Предел размера <c>modinfo.json</c>.</summary>
	private const int maxManifestBytes = 1024 * 1024;

	public static Result<ModManifest> ReadManifest(string path)
	{
		try
		{
			using var archive = ZipFile.OpenRead(path);

			if (archive.Entries.Count > maxEntries)
			{
				return Failure.Invalid("Слишком много файлов в архиве мода");
			}

			foreach (var entry in archive.Entries)
			{
				// Та же проверка, что для резервной копии: архив мода тоже чужой.
				var judged = ArchiveSafety.Judge(ArchiveEntry.CreateFile(entry.FullName));
				if (!judged.IsSuccess)
				{
					return Failure.Invalid($"Небезопасный путь в архиве мода: {entry.FullName}");
				}
			}

			var manifests = archive.Entries
				.Where(entry => entry.FullName.TrimEnd('/') == "modinfo.json")
				.ToArray();

			if (manifests.Length != 1)
			{
				return Failure.Invalid("Архив должен содержать один modinfo.json в корне");
			}

			if (manifests[0].Length > maxManifestBytes)
			{
				return Failure.Invalid("modinfo.json неправдоподобно велик");
			}

			using var stream = manifests[0].Open();
			using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

			return Parse(reader.ReadToEnd());
		}
		catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
		{
			return Failure.Invalid($"Некорректный архив мода: {exception.Message}");
		}
	}

	private static Result<ModManifest> Parse(string json)
	{
		JsonNode? node;
		try
		{
			node = JsonNode.Parse(json);
		}
		catch (JsonException exception)
		{
			return Failure.Invalid($"Некорректный modinfo.json: {exception.Message}");
		}

		if (node is not JsonObject root)
		{
			return Failure.Invalid("modinfo.json не является объектом");
		}

		// Регистр ключей в modinfo.json не закреплён: игра читает их без учёта регистра,
		// и многие моды пишут, например, «ModID».
		var id = ModId.Create(Text(root, "modid"));
		if (!id.IsSuccess)
		{
			return Result.Fail<ModManifest>(id.Failure!);
		}

		var version = Text(root, "version");
		if (version.Length == 0)
		{
			return Failure.Invalid("В modinfo.json нет версии");
		}

		var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (Property(root, "dependencies") is JsonObject declared)
		{
			foreach (var (key, value) in declared)
			{
				dependencies[key] = value?.ToString() ?? string.Empty;
			}
		}

		return new ModManifest(
			id.Value,
			version,
			Text(root, "name") is { Length: > 0 } name ? name : id.Value.Value,
			Text(root, "side") is { Length: > 0 } side ? side : "universal",
			dependencies);
	}

	private static JsonNode? Property(JsonObject root, string name) =>
		root.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

	private static string Text(JsonObject root, string name) =>
		Property(root, name) is JsonValue value && value.TryGetValue<string>(out var text)
			? text.Trim()
			: string.Empty;

	/// <summary>
	/// Имя файла мода в каталоге: <c>идентификатор_версия.zip</c>. Из версии убираются символы,
	/// недопустимые в имени файла.
	/// </summary>
	public static Result<ModArchiveName> FileNameFor(ModManifest manifest)
	{
		var version = new string([.. manifest.Version.Where(IsAllowedInVersion)]);

		return version.Length == 0
			? Failure.Invalid("Версия мода не годится для имени файла")
			: ModArchiveName.Create($"{manifest.Id.Value}_{version}.zip");
	}

	private static bool IsAllowedInVersion(char value) =>
		char.IsAsciiLetterOrDigit(value) || value is '.' or '_' or '+' or '-';
}
