using System.Text.Json.Nodes;
using Panel.SharedKernel;

namespace Panel.Provisioning.Domain;

/// <summary>Перенос путей игры из настроек другой машины в раскладку целевого сервера.</summary>
public sealed record ServerConfigPaths
{
	private ServerConfigPaths(string worldDirectory, string modsDirectory)
	{
		WorldDirectory = worldDirectory;
		ModsDirectory = modsDirectory;
	}

	public string WorldDirectory { get; }
	public string ModsDirectory { get; }

	public static ServerConfigPaths FromLayout(InstanceLayout layout) =>
		new(layout.WorldDirectory, layout.ModsDirectory);

	public static ServerConfigPaths Create(string worldDirectory, string modsDirectory) =>
		new(worldDirectory, modsDirectory);

	/// <summary>Подготовить конфигурацию для пакета, не сохраняя пути исходной машины.</summary>
	public Result<JsonObject> Portable(JsonObject config)
	{
		ArgumentNullException.ThrowIfNull(config);
		var result = (JsonObject) config.DeepClone();
		if (result["WorldConfig"] is JsonObject world && world["SaveFileLocation"] is JsonValue save)
		{
			if (!save.TryGetValue<string>(out var location) || !TryWorldName(location, out var name)
				|| !SamePath(location, PathFor(WorldDirectory, name)))
			{
				return Failure.Invalid("Путь активного мира не принадлежит каталогам сервера");
			}

			world["SaveFileLocation"] = "Saves/" + name;
		}

		if (result["ModPaths"] is JsonArray paths)
		{
			var rewritten = new JsonArray();
			foreach (var node in paths)
			{
				if (node is not JsonValue value || !value.TryGetValue<string>(out var path))
				{
					return Failure.Invalid("Недопустимый путь модов в настройках сервера");
				}

				if (SamePath(path, ModsDirectory))
				{
					rewritten.Add("Mods");
				}
				else if (IsAbsolute(path) || !IsSafeRelativeModPath(path))
				{
					return Failure.Invalid($"Путь модов находится вне каталога сервера: {path}");
				}
				else
				{
					rewritten.Add(path);
				}
			}

			result["ModPaths"] = rewritten;
		}

		return result;
	}

	public Result<JsonObject> Rebase(JsonObject config)
	{
		ArgumentNullException.ThrowIfNull(config);
		var result = (JsonObject) config.DeepClone();
		if (result["WorldConfig"] is JsonObject world && world["SaveFileLocation"] is JsonValue save)
		{
			if (!save.TryGetValue<string>(out var location) || string.IsNullOrWhiteSpace(location))
			{
				return Failure.Invalid("Недопустимый путь активного мира в настройках сервера");
			}

			if (!TryWorldName(location, out var name))
			{
				return Failure.Invalid("Недопустимое имя активного мира в настройках сервера");
			}

			world["SaveFileLocation"] = PathFor(WorldDirectory, name);
		}

		if (result["ModPaths"] is JsonArray paths)
		{
			var rewritten = new JsonArray();
			foreach (var node in paths)
			{
				if (node is not JsonValue value || !value.TryGetValue<string>(out var path))
				{
					return Failure.Invalid("Недопустимый путь модов в настройках сервера");
				}

				var portable = path.Replace('\\', '/');
				if (IsAbsolute(portable))
				{
					if (!rewritten.Any(item => (string?) item == ModsDirectory))
					{
						rewritten.Add(ModsDirectory);
					}
				}
				else
				{
					rewritten.Add(path);
				}
			}

			if (!rewritten.Any(item => (string?) item == ModsDirectory))
			{
				rewritten.Add(ModsDirectory);
			}

			result["ModPaths"] = rewritten;
		}

		return result;
	}

	private static string PathFor(string directory, string name) =>
		directory.TrimEnd('/', '\\') + (directory.Contains('\\') ? "\\" : "/") + name;

	private static bool IsAbsolute(string path) =>
		path.StartsWith('/') || path.StartsWith('\\') || (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]));

	private static bool IsSafeRelativeModPath(string path) =>
		!string.IsNullOrWhiteSpace(path) && !path.Replace('\\', '/').Split('/')
			.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Contains(':'));

	private static bool SamePath(string left, string right)
	{
		var windows = left.Length >= 2 && left[1] == ':';
		return string.Equals(left.Replace('\\', '/').TrimEnd('/'), right.Replace('\\', '/').TrimEnd('/'),
			windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
	}

	private static bool TryWorldName(string location, out string name)
	{
		var segments = location.Replace('\\', '/').Split('/');
		name = segments.Last();
		return name.Length > 0 && !segments.Any(segment => segment is "." or "..")
			&& !name.EndsWith(' ') && !name.EndsWith('.')
			&& !name.Any(char.IsControl) && name.IndexOfAny(":*?\"<>|".ToCharArray()) < 0
			&& name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
			&& name.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase);
	}
}
