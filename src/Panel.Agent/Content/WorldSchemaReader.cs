using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Panel.Configuration.Domain;
using Panel.Configuration.Localization;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Content;

/// <summary>
/// Схема параметров мира: игра плюс включённые моды. Читает демон — он знает <c>program_dir</c> и
/// каталог модов; панель присылает только идентификатор сервера.
/// </summary>
/// <remarks>
/// <para>
/// Сборки читаются как данные через <see cref="System.Reflection.Metadata"/>: таблицы метаданных
/// разбираются без загрузки сборки в процесс и без исполнения чего-либо из неё
/// (<see cref="System.Reflection.Assembly"/> здесь не участвует).
/// </para>
/// <para>
/// Кэш — по ключу «путь + размер + время изменения» каждого источника; обновление игры или мода
/// сбрасывает его само, потому что меняется хотя бы одно из этих трёх значений.
/// </para>
/// </remarks>
public sealed class WorldSchemaReader
{
	/// <summary>Предел строки схемы одного источника, как у <c>modinfo.json</c> в архиве.</summary>
	private const int maxSchemaBytes = 1024 * 1024;

	private const int maxEntries = 20_000;

	/// <summary>Предел сборки внутри архива мода: она читается в память целиком.</summary>
	private const int maxAssemblyBytes = 64 * 1024 * 1024;

	private static readonly string[] gameAssemblies = ["VSSurvivalMod.dll", "VSCreativeMod.dll", "VSEssentials.dll"];

	private readonly ConcurrentDictionary<string, (string Stamp, object Value)> cache = new(StringComparer.Ordinal);

	/// <summary>Схема сервера: игра, затем включённые моды с собственным кодом, в порядке имён файлов.</summary>
	public Result<WorldSchema> Read(RegisteredServer server)
	{
		ArgumentNullException.ThrowIfNull(server);
		var sources = new List<Result<WorldSchema>>();

		var modsDirectory = Path.Combine(server.ProgramDirectory, "Mods");
		foreach (var name in gameAssemblies)
		{
			var path = Path.Combine(modsDirectory, name);
			if (File.Exists(path))
			{
				sources.Add(Cached("schema", [path], () => FromAssembly(path, "game")));
			}
		}

		if (Directory.Exists(server.ModsDirectory))
		{
			foreach (var archive in Directory.EnumerateFiles(server.ModsDirectory, "*.zip").OrderBy(path => path, StringComparer.Ordinal))
			{
				var name = Path.GetFileName(archive);
				sources.Add(Cached("schema", [archive], () => FromModArchive(archive, "mod:" + name)));
			}
		}

		return WorldSchema.Merge(sources);
	}

	/// <summary>
	/// Подписи для схемы: <c>assets/game/lang/{ru,en}.json</c> игры плюс языковые файлы включённых
	/// модов, в том же порядке, что и <see cref="Read"/>.
	/// </summary>
	public WorldLanguagePack ReadLanguage(RegisteredServer server, string primary, string fallback)
	{
		ArgumentNullException.ThrowIfNull(server);
		var assets = Path.Combine(server.ProgramDirectory, "assets");
		var lang = Path.Combine(assets, "game", "lang");
		var primaryPath = Path.Combine(lang, primary + ".json");
		var fallbackPath = Path.Combine(lang, fallback + ".json");

		// Языковые файлы игры — около 3 МБ на двоих: разбираются один раз, пока не изменятся.
		var game = Cached("lang", [primaryPath, fallbackPath], () => WorldLanguagePack.From(ReadSmallJson(primaryPath), ReadSmallJson(fallbackPath)));

		if (!Directory.Exists(server.ModsDirectory))
		{
			return game;
		}

		var packs = new List<WorldLanguagePack> { game };
		foreach (var archive in Directory.EnumerateFiles(server.ModsDirectory, "*.zip").OrderBy(path => path, StringComparer.Ordinal))
		{
			packs.Add(Cached("lang:" + primary + ":" + fallback, [archive], () => ModLanguage(archive, primary, fallback)));
		}

		return WorldLanguagePack.Merge(packs);
	}

	/// <summary>
	/// Значение из кэша, пока у каждого файла прежние размер и время изменения. Ключ — вид значения и
	/// полные пути: у двух серверов одноимённые файлы разные.
	/// </summary>
	private T Cached<T>(string kind, IReadOnlyList<string> paths, Func<T> compute)
	{
		var tag = kind + "|" + string.Join("|", paths);
		string stamp;
		try
		{
			stamp = string.Join("|", paths.Select(path => new FileInfo(path) is { Exists: true } info
				? info.Length + ":" + info.LastWriteTimeUtc.Ticks
				: "-"));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return compute();
		}

		if (cache.TryGetValue(tag, out var hit) && hit.Stamp == stamp)
		{
			return (T) hit.Value;
		}

		var value = compute()!;
		cache[tag] = (stamp, value);
		return value;
	}

	// ---------- сборки ----------

	private static Result<WorldSchema> FromAssembly(string path, string source)
	{
		try
		{
			using var stream = File.OpenRead(path);
			return FromAssemblyStream(stream, source);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException)
		{
			return Failure.Invalid($"{source}: не удалось прочитать сборку: {exception.Message}");
		}
	}

	private static Result<WorldSchema> FromAssemblyStream(Stream stream, string source)
	{
		using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
		if (!reader.HasMetadata)
		{
			return WorldSchema.Empty;
		}

		var metadata = reader.GetMetadataReader();
		var config = ReadModInfoWorldConfig(metadata);
		return config is { Length: > 0 } json
			? json.Length <= maxSchemaBytes
				? WorldSchema.ParseSource(json, source)
				: Failure.Invalid($"{source}: строка схемы мира неправдоподобно велика")
			: WorldSchema.Empty;
	}

	/// <summary>
	/// Значение именованного аргумента <c>WorldConfig</c> атрибута сборки <c>ModInfoAttribute</c>,
	/// если он есть. Раскодирует блоб атрибута без разрешения типа мода: тип аргумента известен
	/// (строка), фиксированные аргументы конструктора не используются.
	/// </summary>
	private static string? ReadModInfoWorldConfig(MetadataReader metadata)
	{
		foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
		{
			var attribute = metadata.GetCustomAttribute(handle);
			if (AttributeTypeName(metadata, attribute) != "ModInfoAttribute")
			{
				continue;
			}

			try
			{
				var value = attribute.DecodeValue(FlatAttributeTypeProvider.Instance);
				foreach (var argument in value.NamedArguments)
				{
					if (argument.Name == "WorldConfig" && argument.Value is string text)
					{
						return text;
					}
				}
			}
			catch (Exception exception) when (exception is BadImageFormatException or InvalidOperationException or NotSupportedException or ArgumentException)
			{
				// Атрибут не в ожидаемой форме — пропускается, а не роняет разбор всей сборки.
			}
		}

		return null;
	}

	private static string? AttributeTypeName(MetadataReader metadata, CustomAttribute attribute)
	{
		var constructor = attribute.Constructor;
		if (constructor.Kind == HandleKind.MemberReference)
		{
			var member = metadata.GetMemberReference((MemberReferenceHandle) constructor);
			return member.Parent.Kind switch
			{
				HandleKind.TypeReference => metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle) member.Parent).Name),
				HandleKind.TypeDefinition => metadata.GetString(metadata.GetTypeDefinition((TypeDefinitionHandle) member.Parent).Name),
				_ => null,
			};
		}

		if (constructor.Kind == HandleKind.MethodDefinition)
		{
			var method = metadata.GetMethodDefinition((MethodDefinitionHandle) constructor);
			return metadata.GetString(metadata.GetTypeDefinition(method.GetDeclaringType()).Name);
		}

		return null;
	}

	// ---------- архивы модов ----------

	private static Result<WorldSchema> FromModArchive(string path, string source)
	{
		try
		{
			using var archive = ZipFile.OpenRead(path);
			if (archive.Entries.Count > maxEntries)
			{
				return Failure.Invalid($"{source}: слишком много файлов в архиве");
			}

			var manifest = archive.Entries.FirstOrDefault(entry => entry.FullName.TrimEnd('/') == "modinfo.json");
			if (manifest is not null && manifest.Length <= maxSchemaBytes)
			{
				var fromManifest = WorldConfigFromManifest(manifest);
				if (fromManifest is { Length: > 0 })
				{
					return WorldSchema.ParseSource(fromManifest, source);
				}
			}

			// Мод без ключа worldConfig в modinfo.json может объявлять схему в своей .dll, как игра.
			var dll = archive.Entries.FirstOrDefault(entry => !entry.FullName.Contains('/') && entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
			if (dll is null)
			{
				return WorldSchema.Empty;
			}

			if (dll.Length > maxAssemblyBytes)
			{
				return Failure.Invalid($"{source}: сборка мода неправдоподобно велика");
			}

			// Длина из каталога архива может лгать: читается не больше предела.
			using var entryStream = dll.Open();
			using var buffer = new MemoryStream();
			var chunk = new byte[81920];
			int read;
			while ((read = entryStream.Read(chunk)) > 0)
			{
				if (buffer.Length + read > maxAssemblyBytes)
				{
					return Failure.Invalid($"{source}: сборка мода неправдоподобно велика");
				}

				buffer.Write(chunk, 0, read);
			}

			buffer.Position = 0;
			return FromAssemblyStream(buffer, source);
		}
		catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or BadImageFormatException)
		{
			return Failure.Invalid($"{source}: не удалось прочитать архив: {exception.Message}");
		}
	}

	private static string? WorldConfigFromManifest(ZipArchiveEntry manifest)
	{
		using var stream = manifest.Open();
		using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		try
		{
			var node = System.Text.Json.Nodes.JsonNode.Parse(reader.ReadToEnd());
			if (node is not System.Text.Json.Nodes.JsonObject root)
			{
				return null;
			}

			var key = root.Select(pair => pair.Key).FirstOrDefault(name => string.Equals(name, "worldConfig", StringComparison.OrdinalIgnoreCase));
			if (key is null)
			{
				return null;
			}

			return root[key] switch
			{
				System.Text.Json.Nodes.JsonObject config => config.ToJsonString(),
				System.Text.Json.Nodes.JsonValue value when value.TryGetValue<string>(out var text) => text,
				_ => null,
			};
		}
		catch (Exception exception) when (exception is System.Text.Json.JsonException or ArgumentException)
		{
			// ArgumentException — повторяющийся ключ: JsonObject бросает его при переборе.
			return null;
		}
	}

	// ---------- языковые файлы ----------

	private static WorldLanguagePack ModLanguage(string archivePath, string primary, string fallback)
	{
		try
		{
			using var archive = ZipFile.OpenRead(archivePath);
			if (archive.Entries.Count > maxEntries)
			{
				return WorldLanguagePack.Empty;
			}

			string? Find(string language) => archive.Entries
				.Where(entry => entry.FullName.EndsWith($"lang/{language}.json", StringComparison.OrdinalIgnoreCase) && entry.Length <= maxSchemaBytes)
				.Select(ReadEntry)
				.FirstOrDefault(text => text is not null);

			return WorldLanguagePack.From(Find(primary), Find(fallback));
		}
		catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
		{
			return WorldLanguagePack.Empty;
		}
	}

	private static string? ReadEntry(ZipArchiveEntry entry)
	{
		using var stream = entry.Open();
		using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return reader.ReadToEnd();
	}

	private static string? ReadSmallJson(string path)
	{
		try
		{
			return File.Exists(path) ? File.ReadAllText(path) : null;
		}
		catch (IOException)
		{
			return null;
		}
	}

	/// <summary>
	/// Раскодирует только то, что встречается в <c>ModInfoAttribute</c>: строки, простые типы и их
	/// массивы. Тип аргумента, который здесь не нужен (например <c>Type</c> или перечисление),
	/// не встречается в этом атрибуте — соответствующие методы не вызываются.
	/// </summary>
	private sealed class FlatAttributeTypeProvider : ICustomAttributeTypeProvider<string>
	{
		public static readonly FlatAttributeTypeProvider Instance = new();

		public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

		public string GetSZArrayType(string elementType) => elementType + "[]";

		public string GetSystemType() => "System.Type";

		public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
			reader.GetString(reader.GetTypeDefinition(handle).Name);

		public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
			reader.GetString(reader.GetTypeReference(handle).Name);

		public string GetTypeFromSerializedName(string name) => name;

		public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;

		public bool IsSystemType(string type) => type == "System.Type";
	}
}
