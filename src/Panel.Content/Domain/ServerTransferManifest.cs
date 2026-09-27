using System.Text;
using System.Text.Json;
using Panel.SharedKernel;

namespace Panel.Content.Domain;

/// <summary>Проверенные метаданные одного файла в переносимом пакете сервера.</summary>
public sealed record TransferFile(string Path, long Length, string Sha256);

/// <summary>Манифест версии 1: только файлы данных и активный мир, без учётных данных.</summary>
public sealed class ServerTransferManifest
{
	public const int CurrentVersion = 1;
	public const int MaximumManifestBytes = 16 * 1024 * 1024;

	public const int MaximumGameVersionLength = 64;

	private ServerTransferManifest(string activeWorld, string gameVersion, IReadOnlyList<TransferFile> files)
	{
		ActiveWorld = activeWorld;
		GameVersion = gameVersion;
		Files = files;
	}

	public int Version => CurrentVersion;

	public string ActiveWorld { get; }

	/// <summary>Версия игры источника; пустая строка — неизвестна.</summary>
	public string GameVersion { get; }

	public IReadOnlyList<TransferFile> Files { get; }

	/// <summary>Создать манифест из измеренных файлов пакета до записи архива.</summary>
	public static Result<ServerTransferManifest> Create(string activeWorld, IReadOnlyList<TransferFile> files, string gameVersion = "")
	{
		if (activeWorld is null || gameVersion is null || files is null || files.Count is 0 or > ServerTransferArchive.MaximumEntries
			|| files.Any(file => file is null))
		{
			return Failure.Invalid("Неверные файлы или активный мир манифеста сервера");
		}

		return Parse(JsonSerializer.SerializeToUtf8Bytes(new
		{
			version = CurrentVersion,
			active_world = activeWorld,
			game_version = gameVersion,
			files = files.Select(file => new { path = file.Path, length = file.Length, sha256 = file.Sha256 }),
		}));
	}

	/// <summary>Прочитать ограниченный по размеру манифест без замены некорректного UTF-8.</summary>
	public static Result<ServerTransferManifest> Parse(ReadOnlySpan<byte> json)
	{
		if (json.Length > MaximumManifestBytes)
		{
			return Failure.Invalid("Манифест сервера отсутствует или слишком велик");
		}

		try
		{
			return Parse(new UTF8Encoding(false, true).GetString(json));
		}
		catch (DecoderFallbackException)
		{
			return Failure.Invalid("Манифест сервера не является допустимым UTF-8");
		}
	}

	/// <summary>Записать только поля проверенной схемы версии 1.</summary>
	public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(new
	{
		version = Version,
		active_world = ActiveWorld,
		game_version = GameVersion,
		files = Files.Select(file => new { path = file.Path, length = file.Length, sha256 = file.Sha256 }),
	});

	/// <summary>Прочитать строго заданную схему без игнорирования новых или повторных полей.</summary>
	public static Result<ServerTransferManifest> Parse(string json)
	{
		if (json is null || Encoding.UTF8.GetByteCount(json) > MaximumManifestBytes)
		{
			return Failure.Invalid("Манифест сервера отсутствует или слишком велик");
		}

		try
		{
			using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8, CommentHandling = JsonCommentHandling.Disallow });
			var root = document.RootElement;
			if (!HasKeys(root, "version", "active_world", "game_version", "files")
				|| root.GetProperty("version").ValueKind != JsonValueKind.Number
				|| !root.GetProperty("version").TryGetInt32(out var version) || version != CurrentVersion
				|| root.GetProperty("active_world").ValueKind != JsonValueKind.String
				|| root.GetProperty("game_version").ValueKind != JsonValueKind.String
				|| root.GetProperty("files").ValueKind != JsonValueKind.Array)
			{
				return Failure.Invalid("Неверная схема или версия манифеста сервера");
			}

			var activeWorld = root.GetProperty("active_world").GetString()!;
			var gameVersion = root.GetProperty("game_version").GetString()!;
			if (gameVersion.Length > MaximumGameVersionLength
				|| !gameVersion.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+' or '_'))
			{
				return Failure.Invalid("Недопустимая версия игры в манифесте сервера");
			}

			var items = root.GetProperty("files");
			if (items.GetArrayLength() is 0 or > ServerTransferArchive.MaximumEntries)
			{
				return Failure.Invalid("Неверное количество файлов в манифесте сервера");
			}

			var files = new List<TransferFile>(items.GetArrayLength());
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			long total = 0;
			foreach (var item in items.EnumerateArray())
			{
				if (!HasKeys(item, "path", "length", "sha256")
					|| item.GetProperty("path").ValueKind != JsonValueKind.String
					|| item.GetProperty("length").ValueKind != JsonValueKind.Number
					|| !item.GetProperty("length").TryGetInt64(out var length)
					|| item.GetProperty("sha256").ValueKind != JsonValueKind.String)
				{
					return Failure.Invalid("Неверная запись файла в манифесте сервера");
				}

				var path = item.GetProperty("path").GetString()!;
				var hash = item.GetProperty("sha256").GetString()!;
				var checkedPath = ServerTransferArchive.JudgeEntry(ArchiveEntry.CreateFile(path));
				var counted = ServerTransferArchive.CountBytes(total, length);
				if (!checkedPath.IsSuccess || path == ServerTransferArchive.Manifest || !seen.Add(path)
					|| path == "data/serverconfig.json" && length > ServerTransferArchive.MaximumConfigBytes
					|| !ValidHash(hash) || !counted.IsSuccess)
				{
					return Failure.Invalid("Недопустимый путь, размер или SHA256 файла в манифесте сервера");
				}

				total = counted.Value;
				files.Add(new TransferFile(path, length, hash));
			}

			var layout = ServerTransferArchive.Judge(files.Select(file => ArchiveEntry.CreateFile(file.Path))
				.Prepend(ArchiveEntry.CreateFile(ServerTransferArchive.Manifest)));
			if (!layout.IsSuccess || !activeWorld.StartsWith("data/Saves/", StringComparison.Ordinal)
				|| activeWorld["data/Saves/".Length..].Contains('/')
				|| !activeWorld.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase)
				|| !files.Any(file => file.Path == activeWorld && file.Length > 0))
			{
				return Failure.Invalid("Активный мир отсутствует в манифесте сервера или пуст");
			}

			return new ServerTransferManifest(activeWorld, gameVersion, files.AsReadOnly());
		}
		catch (JsonException)
		{
			return Failure.Invalid("Манифест сервера не является допустимым JSON");
		}
	}

	/// <summary>Сравнить полный набор прочитанных файлов с манифестом, включая длину и SHA256.</summary>
	public Result<Done> Verify(IReadOnlyList<TransferFile> actualFiles)
	{
		ArgumentNullException.ThrowIfNull(actualFiles);
		if (actualFiles.Count != Files.Count)
		{
			return Failure.Invalid("Список файлов пакета не совпадает с манифестом сервера");
		}

		var expected = Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
		foreach (var actual in actualFiles)
		{
			if (actual is null || actual.Path is null || !expected.Remove(actual.Path, out var file)
				|| actual.Length != file.Length || !string.Equals(actual.Sha256, file.Sha256, StringComparison.Ordinal))
			{
				return Failure.Invalid("Файл пакета не совпадает с манифестом сервера");
			}
		}

		return Done.Ok;
	}

	private static bool ValidHash(string value) => value.Length == 64 && value.All(character =>
		character is >= '0' and <= '9' or >= 'a' and <= 'f');

	private static bool HasKeys(JsonElement element, params string[] names)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			return false;
		}

		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var property in element.EnumerateObject())
		{
			if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
			{
				return false;
			}
		}

		return seen.Count == names.Length;
	}
}
