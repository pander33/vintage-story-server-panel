using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Content.Domain;
using Panel.Content.Ports;
using Panel.Contracts;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Content;

/// <summary>
/// Моды одного сервера на диске.
/// </summary>
/// <remarks>
/// Включённый мод лежит в <c>Mods</c>, отключённый — в <c>Mods/.disabled</c>, заменённый —
/// в <c>Mods/.history/&lt;идентификатор&gt;</c>. Файлы не удаляются: замена мода чаще всего
/// ломает запуск сервера, и откат должен сводиться к перемещению файла.
/// </remarks>
public sealed class ModBackend
{
	private const string disabledDirectory = ".disabled";
	private const string historyDirectory = ".history";
	private const string profilesFile = ".profiles.json";

	/// <summary>Сколько сохранённых версий мода показывать.</summary>
	private const int historyDepth = 20;

	private readonly StagingArea staging;
	private readonly TimeProvider clock;

	public ModBackend(StagingArea staging, TimeProvider clock)
	{
		ArgumentNullException.ThrowIfNull(staging);
		ArgumentNullException.ThrowIfNull(clock);

		this.staging = staging;
		this.clock = clock;
	}

	public Result<ModOverview> List(RegisteredServer server)
	{
		var enabled = server.ModsDirectory;
		if (enabled.Length == 0)
		{
			return Failure.Unavailable("Каталог модов не задан в реестре");
		}

		Directory.CreateDirectory(enabled);

		var mods = Read(enabled, isEnabled: true)
			.Concat(Read(Path.Combine(enabled, disabledDirectory), isEnabled: false))
			.ToArray();

		return new ModOverview(ModCatalogue.Of(mods), ReadHistory(enabled), ReadProfileNames(enabled));
	}

	public async Task<Result<ModPlacement>> InstallAsync(
		RegisteredServer server,
		StagedFile staged,
		CancellationToken ct)
	{
		var source = await staging.ResolveAsync(staged, ".zip", ct).ConfigureAwait(false);
		if (!source.IsSuccess)
		{
			return Result.Fail<ModPlacement>(source.Failure!);
		}

		var manifest = ModArchive.ReadManifest(source.Value);
		if (!manifest.IsSuccess)
		{
			return Result.Fail<ModPlacement>(manifest.Failure!);
		}

		var name = ModArchive.FileNameFor(manifest.Value);
		if (!name.IsSuccess)
		{
			return Result.Fail<ModPlacement>(name.Failure!);
		}

		// История пишется до установки: если запись нового файла сорвётся, прежняя версия
		// уже сохранена.
		var archived = ArchivePreviousVersions(server.ModsDirectory, manifest.Value.Id);

		var written = await GameFiles.ReplaceAsync(
			source.Value,
			Path.Combine(server.ModsDirectory, name.Value.Value),
			UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
			ownedByGame: true,
			ct).ConfigureAwait(false);

		return written.IsSuccess
			? new ModPlacement(name.Value, manifest.Value.Id, manifest.Value.Version, archived)
			: Result.Fail<ModPlacement>(written.Failure!);
	}

	public Result<InstalledMod> Toggle(RegisteredServer server, ModArchiveName mod)
	{
		var enabledPath = Path.Combine(server.ModsDirectory, mod.Value);
		var disabledDirectory = Path.Combine(server.ModsDirectory, ModBackend.disabledDirectory);
		var disabledPath = Path.Combine(disabledDirectory, mod.Value);

		Directory.CreateDirectory(disabledDirectory);

		if (GameFiles.IsPlainFile(enabledPath))
		{
			if (Path.Exists(disabledPath))
			{
				return Failure.Conflict("Отключённый файл с таким именем уже существует");
			}

			File.Move(enabledPath, disabledPath);
			return Describe(new FileInfo(disabledPath), enabled: false);
		}

		if (GameFiles.IsPlainFile(disabledPath))
		{
			if (Path.Exists(enabledPath))
			{
				return Failure.Conflict("Включённый файл с таким именем уже существует");
			}

			File.Move(disabledPath, enabledPath);
			return Describe(new FileInfo(enabledPath), enabled: true);
		}

		return Failure.NotFound("Мод не найден");
	}

	/// <summary>
	/// Удаление мода — перемещение в историю. Вернуть его можно той же командой, что и
	/// после обновления.
	/// </summary>
	public Result<ModRemoval> Delete(RegisteredServer server, ModArchiveName mod)
	{
		var source = new[]
			{
				Path.Combine(server.ModsDirectory, mod.Value),
				Path.Combine(server.ModsDirectory, disabledDirectory, mod.Value),
			}
			.FirstOrDefault(GameFiles.IsPlainFile);

		if (source is null)
		{
			return Failure.NotFound("Мод не найден");
		}

		// Идентификатор берётся из архива; нечитаемый архив попадает в каталог «unknown».
		var manifest = ModArchive.ReadManifest(source);
		var folder = manifest.IsSuccess ? manifest.Value.Id.Value : "unknown";
		var history = Path.Combine(server.ModsDirectory, historyDirectory, folder);
		Directory.CreateDirectory(history);

		var target = GameFiles.FreeName(history, GameFiles.Stamp(clock.GetUtcNow()), mod.Value);
		File.Move(source, target);

		return new ModRemoval(mod, Path.GetFileName(target));
	}

	public async Task<Result<ModPlacement>> RollbackAsync(
		RegisteredServer server,
		ModId mod,
		ModArchiveName archived,
		CancellationToken ct)
	{
		var source = Path.Combine(server.ModsDirectory, historyDirectory, mod.Value, archived.Value);
		if (!GameFiles.IsPlainFile(source))
		{
			return Failure.NotFound("Сохранённая версия мода не найдена");
		}

		var manifest = ModArchive.ReadManifest(source);
		if (!manifest.IsSuccess)
		{
			return Result.Fail<ModPlacement>(manifest.Failure!);
		}

		if (manifest.Value.Id != mod)
		{
			// Архив в истории мода объявляет другой идентификатор: каталог правили вручную
			// или файл подменён. Ставить его нельзя.
			return Failure.Conflict("Сохранённый архив объявляет другой идентификатор мода");
		}

		var name = ModArchive.FileNameFor(manifest.Value);
		if (!name.IsSuccess)
		{
			return Result.Fail<ModPlacement>(name.Failure!);
		}

		var replaced = ArchivePreviousVersions(server.ModsDirectory, mod);

		var written = await GameFiles.ReplaceAsync(
			source,
			Path.Combine(server.ModsDirectory, name.Value.Value),
			UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
			ownedByGame: true,
			ct).ConfigureAwait(false);

		if (!written.IsSuccess)
		{
			return Result.Fail<ModPlacement>(written.Failure!);
		}

		File.Delete(source);
		return new ModPlacement(name.Value, mod, manifest.Value.Version, replaced);
	}

	public Result<ModProfile> SaveProfile(RegisteredServer server, ModProfileName profile)
	{
		var files = Read(server.ModsDirectory, isEnabled: true)
			.Select(mod => mod.File)
			.OrderBy(file => file.Value, StringComparer.Ordinal)
			.ToArray();

		var profiles = ReadProfiles(server.ModsDirectory);
		profiles[profile.Value] = new JsonArray([.. files.Select(file => JsonValue.Create(file.Value))]);

		return WriteProfiles(server.ModsDirectory, profiles)
			.Map(_ => new ModProfile(profile, files));
	}

	public Result<ModProfile> ApplyProfile(RegisteredServer server, ModProfileName profile)
	{
		var overview = List(server);
		if (!overview.IsSuccess)
		{
			return Result.Fail<ModProfile>(overview.Failure!);
		}

		var stored = ReadProfile(server.ModsDirectory, profile);
		if (!stored.IsSuccess)
		{
			return stored;
		}

		var applicable = stored.Value.CanApplyTo(overview.Value.Catalogue);
		if (!applicable.IsSuccess)
		{
			return Result.Fail<ModProfile>(applicable.Failure!);
		}

		var wanted = stored.Value.Files.ToHashSet();
		var disabledDirectory = Path.Combine(server.ModsDirectory, ModBackend.disabledDirectory);
		Directory.CreateDirectory(disabledDirectory);

		foreach (var mod in overview.Value.Catalogue.Mods)
		{
			var enabledPath = Path.Combine(server.ModsDirectory, mod.File.Value);
			var disabledPath = Path.Combine(disabledDirectory, mod.File.Value);

			if (wanted.Contains(mod.File) && !mod.Enabled)
			{
				File.Move(disabledPath, enabledPath);
			}
			else if (!wanted.Contains(mod.File) && mod.Enabled)
			{
				File.Move(enabledPath, disabledPath);
			}
		}

		return stored;
	}

	public Result<Done> DeleteProfile(RegisteredServer server, ModProfileName profile)
	{
		var profiles = ReadProfiles(server.ModsDirectory);

		return profiles.Remove(profile.Value)
			? WriteProfiles(server.ModsDirectory, profiles)
			: Failure.NotFound("Профиль модов не найден");
	}

	public Result<IReadOnlyList<ModConfigEntry>> ListConfigs(RegisteredServer server)
	{
		if (server.ModConfigDirectory.Length == 0)
		{
			return Failure.Unavailable("Каталог настроек модов не задан");
		}

		Directory.CreateDirectory(server.ModConfigDirectory);

		var entries = new List<ModConfigEntry>();
		foreach (var path in Directory.EnumerateFiles(server.ModConfigDirectory, "*.json"))
		{
			var info = new FileInfo(path);
			if (info is not { Exists: true, LinkTarget: null })
			{
				continue;
			}

			var name = ModConfigName.Create(info.Name);
			if (name.IsSuccess)
			{
				entries.Add(new ModConfigEntry(name.Value, info.Length, info.LastWriteTimeUtc));
			}
		}

		entries.Sort((left, right) =>
			string.Compare(left.Name.Value, right.Name.Value, StringComparison.OrdinalIgnoreCase));

		return Result.Ok<IReadOnlyList<ModConfigEntry>>(entries);
	}

	/// <summary>Настройки мода целиком, с пределом размера.</summary>
	public async Task<Result<ModConfigDocument>> ReadConfigAsync(
		RegisteredServer server,
		ModConfigName name,
		CancellationToken ct)
	{
		var path = Path.Combine(server.ModConfigDirectory, name.Value);
		var info = new FileInfo(path);

		if (info is not { Exists: true, LinkTarget: null })
		{
			return Failure.NotFound("Файл настроек не найден");
		}

		if (info.Length > 2 * 1024 * 1024)
		{
			return Failure.Invalid("Файл настроек слишком велик для редактора");
		}

		var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);

		try
		{
			JsonNode.Parse(json);
		}
		catch (JsonException exception)
		{
			return Failure.Invalid($"Файл настроек повреждён: {exception.Message}");
		}

		return new ModConfigDocument(name, json, info.LastWriteTimeUtc.Ticks);
	}

	public async Task<Result<Done>> WriteConfigAsync(
		RegisteredServer server,
		ModConfigName name,
		StagedFile staged,
		CancellationToken ct)
	{
		var source = await staging.ResolveAsync(staged, ".json", ct).ConfigureAwait(false);
		if (!source.IsSuccess)
		{
			return Result.Fail<Done>(source.Failure!);
		}

		var json = await File.ReadAllTextAsync(source.Value, ct).ConfigureAwait(false);
		try
		{
			if (JsonNode.Parse(json) is not (JsonObject or JsonArray))
			{
				return Failure.Invalid("Настройки мода должны быть объектом или массивом");
			}
		}
		catch (JsonException exception)
		{
			return Failure.Invalid($"Некорректный JSON: {exception.Message}");
		}

		var target = Path.Combine(server.ModConfigDirectory, name.Value);
		if (Path.Exists(target))
		{
			if (!GameFiles.IsPlainFile(target))
			{
				return Failure.Conflict("Цель не является обычным файлом");
			}

			File.Copy(target, $"{target}.panel-{GameFiles.Stamp(clock.GetUtcNow())}.bak", overwrite: true);
		}

		return await GameFiles.ReplaceAsync(
			source.Value,
			target,
			UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead,
			ownedByGame: true,
			ct).ConfigureAwait(false);
	}

	/// <summary>
	/// Убрать в историю все установленные версии мода. Возвращает имена в истории, по ним
	/// делается откат.
	/// </summary>
	private IReadOnlyList<string> ArchivePreviousVersions(string modsDirectory, ModId mod)
	{
		var history = Path.Combine(modsDirectory, historyDirectory, mod.Value);
		Directory.CreateDirectory(history);

		var stamp = GameFiles.Stamp(clock.GetUtcNow());
		var archived = new List<string>();

		foreach (var directory in new[] { modsDirectory, Path.Combine(modsDirectory, disabledDirectory) })
		{
			if (!Directory.Exists(directory))
			{
				continue;
			}

			foreach (var path in Directory.EnumerateFiles(directory, "*.zip"))
			{
				if (ModArchive.ReadManifest(path) is not { IsSuccess: true } manifest || manifest.Value.Id != mod)
				{
					continue;
				}

				var target = GameFiles.FreeName(history, stamp, Path.GetFileName(path));
				File.Move(path, target);
				archived.Add(Path.GetFileName(target));
			}
		}

		return archived;
	}

	private static IEnumerable<InstalledMod> Read(string directory, bool isEnabled)
	{
		if (!Directory.Exists(directory))
		{
			yield break;
		}

		foreach (var path in Directory.EnumerateFiles(directory, "*.zip"))
		{
			var info = new FileInfo(path);
			if (info is not { Exists: true, LinkTarget: null })
			{
				continue;
			}

			var described = Describe(info, isEnabled);
			if (described.IsSuccess)
			{
				yield return described.Value;
			}
		}
	}

	private static Result<InstalledMod> Describe(FileInfo file, bool enabled)
	{
		var name = ModArchiveName.Create(file.Name);
		if (!name.IsSuccess)
		{
			return Result.Fail<InstalledMod>(name.Failure!);
		}

		var manifest = ModArchive.ReadManifest(file.FullName);

		return manifest.IsSuccess
			? new InstalledMod(
				name.Value,
				enabled,
				file.Length,
				file.LastWriteTimeUtc,
				manifest.Value.Id,
				manifest.Value.Version,
				manifest.Value.DisplayName,
				manifest.Value.Side,
				manifest.Value.Dependencies,
				Unreadable: false)
			: new InstalledMod(
				name.Value,
				enabled,
				file.Length,
				file.LastWriteTimeUtc,
				default,
				string.Empty,
				file.Name,
				string.Empty,
				new Dictionary<string, string>(),
				Unreadable: true);
	}

	private static IReadOnlyList<ModHistory> ReadHistory(string modsDirectory)
	{
		var root = Path.Combine(modsDirectory, historyDirectory);
		if (!Directory.Exists(root))
		{
			return [];
		}

		var result = new List<ModHistory>();
		foreach (var directory in Directory.EnumerateDirectories(root))
		{
			var mod = ModId.Create(Path.GetFileName(directory));
			if (!mod.IsSuccess)
			{
				continue;
			}

			var versions = Directory.EnumerateFiles(directory, "*.zip")
				.Select(Path.GetFileName)
				.OrderDescending(StringComparer.Ordinal)
				.Take(historyDepth)
				.Select(ModArchiveName.Create)
				.Where(name => name.IsSuccess)
				.Select(name => name.Value)
				.ToArray();

			if (versions.Length > 0)
			{
				result.Add(new ModHistory(mod.Value, versions));
			}
		}

		return result;
	}

	private static JsonObject ReadProfiles(string modsDirectory)
	{
		try
		{
			return JsonNode.Parse(File.ReadAllText(Path.Combine(modsDirectory, profilesFile))) as JsonObject ?? [];
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
		{
			// Испорченный или отсутствующий файл профилей означает, что профилей нет.
			return [];
		}
	}

	private static Result<ModProfile> ReadProfile(string modsDirectory, ModProfileName profile)
	{
		if (ReadProfiles(modsDirectory)[profile.Value] is not JsonArray stored)
		{
			return Failure.NotFound("Профиль модов не найден");
		}

		var files = new List<ModArchiveName>();
		foreach (var item in stored)
		{
			var name = ModArchiveName.Create(item?.ToString());
			if (!name.IsSuccess)
			{
				return Failure.Invalid("Профиль модов содержит недопустимое имя файла");
			}

			files.Add(name.Value);
		}

		return new ModProfile(profile, files);
	}

	private static IReadOnlyList<ModProfileName> ReadProfileNames(string modsDirectory) =>
	[
		.. ReadProfiles(modsDirectory)
			.Select(pair => ModProfileName.Create(pair.Key))
			.Where(name => name.IsSuccess)
			.Select(name => name.Value)
			.OrderBy(name => name.Value, StringComparer.Ordinal),
	];

	private static Result<Done> WriteProfiles(string modsDirectory, JsonObject profiles)
	{
		var path = Path.Combine(modsDirectory, profilesFile);
		var temporary = path + $".panel-{Environment.ProcessId}";

		try
		{
			File.WriteAllText(temporary, profiles.ToJsonString(PanelJson.Indented));
			GameFiles.SetMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			File.Move(temporary, path, overwrite: true);
			return Done.Ok;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Не удалось сохранить профили модов: {exception.Message}");
		}
	}
}
