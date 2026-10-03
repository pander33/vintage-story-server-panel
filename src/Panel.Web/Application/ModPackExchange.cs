using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Content.Domain;
using Panel.Content.Ports;
using Panel.Contracts;
using Panel.Contracts.Content;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>Прочитанная сборка: опись и разобранные файлы настроек с исходным текстом.</summary>
public sealed record ModPackContents(
	ModPackManifestPayload Manifest,
	IReadOnlyDictionary<ModConfigName, JsonNode> Configs,
	IReadOnlyDictionary<ModConfigName, byte[]> Files);

/// <summary>
/// Обмен сборками модов: выгрузка с сервера, разбор чужой сборки и применение её настроек и
/// включения модов. Суждение о записях и описи — <see cref="ModPack"/>.
/// </summary>
public static class ModPackExchange
{
	/// <summary>Сборка сервера: моды с modid и файлы настроек, которые читает редактор.</summary>
	public static async Task<Result<byte[]>> ExportAsync(
		IModStore mods,
		InstanceId instance,
		GameVersionText game,
		DateTimeOffset now,
		CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(mods);

		var listed = await mods.ListAsync(instance, ct).ConfigureAwait(false);
		if (!listed.IsSuccess)
		{
			return Result.Fail<byte[]>(listed.Failure!);
		}

		var configs = await mods.ListConfigsAsync(instance, ct).ConfigureAwait(false);
		if (!configs.IsSuccess)
		{
			return Result.Fail<byte[]>(configs.Failure!);
		}

		var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
		var skipped = new List<ModPackSkippedConfigPayload>();
		foreach (var entry in configs.Value.Files.OrderBy(file => file.Name, StringComparer.Ordinal))
		{
			var name = ModConfigName.Create(entry.Name);
			if (!name.IsSuccess)
			{
				continue;
			}

			if (entry.Size > ModPack.MaximumConfigBytes)
			{
				skipped.Add(new ModPackSkippedConfigPayload { Name = entry.Name, Reason = Phrase.From("Файл больше 2 МБ") });
				continue;
			}

			var read = await mods.ReadConfigAsync(instance, name.Value, ct).ConfigureAwait(false);
			if (!read.IsSuccess || read.Value.Config is null)
			{
				skipped.Add(new ModPackSkippedConfigPayload { Name = entry.Name, Reason = read.Failure?.Text ?? Phrase.From("Файл пуст") });
				continue;
			}

			files[name.Value.Value] = Encoding.UTF8.GetBytes(read.Value.Config.ToJsonString(PanelJson.Indented));
		}

		var manifest = new ModPackManifestPayload
		{
			Format = ModPack.Format,
			Version = ModPack.FormatVersion,
			ExportedAt = now,
			Server = instance.Value,
			GameVersion = game.Value,
			Mods =
			[
				.. listed.Value.Mods
					.Where(mod => ModId.Create(mod.ModId).IsSuccess)
					.GroupBy(mod => ModId.Create(mod.ModId).Value.Value, StringComparer.Ordinal)

					// Из повторяющихся архивов одного мода в сборку идёт включённый.
					.Select(group => group.OrderByDescending(mod => mod.Enabled).First())
					.OrderBy(mod => mod.ModId, StringComparer.OrdinalIgnoreCase)
					.Select(mod => new ModPackModPayload
					{
						ModId = ModId.Create(mod.ModId).Value.Value,
						Version = mod.Version ?? string.Empty,
						Enabled = mod.Enabled,
						DisplayName = mod.DisplayName,
						Side = mod.Side,
						File = mod.Name,
					}),
			],
			Configs = [.. files.Keys],
			SkippedConfigs = skipped,
		};

		return Write(manifest, files);
	}

	/// <summary>ZIP сборки: опись первой, затем <c>config/</c>.</summary>
	public static byte[] Write(ModPackManifestPayload manifest, IReadOnlyDictionary<string, byte[]> configs)
	{
		ArgumentNullException.ThrowIfNull(manifest);
		ArgumentNullException.ThrowIfNull(configs);

		using var buffer = new MemoryStream();
		using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
		{
			Add(zip, ModPack.ManifestName, JsonSerializer.SerializeToUtf8Bytes(manifest, PanelJson.Indented));
			foreach (var (name, bytes) in configs)
			{
				Add(zip, $"{ModPack.ConfigDirectory}/{name}", bytes);
			}
		}

		return buffer.ToArray();
	}

	/// <summary>
	/// Прочитать сборку. Сначала суждение обо всех записях и объявленных размерах, потом чтение с
	/// пересчётом: объявленной длине ZIP верить нельзя.
	/// </summary>
	public static Result<ModPackContents> Read(Stream source)
	{
		ArgumentNullException.ThrowIfNull(source);

		ZipArchive zip;
		try
		{
			zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
		}
		catch (InvalidDataException)
		{
			return Failure.Invalid("Файл сборки не является ZIP-архивом");
		}

		using (zip)
		{
			try
			{
				return Read(zip);
			}
			catch (InvalidDataException)
			{
				return Failure.Invalid("ZIP-архив сборки повреждён");
			}
		}
	}

	/// <summary>План применения сборки к серверу; настройки сервера читаются только те, что есть в сборке.</summary>
	public static async Task<Result<ModPackPlanPayload>> PlanAsync(
		IModStore mods,
		InstanceId instance,
		ModPackContents contents,
		GameVersionText game,
		CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(mods);
		ArgumentNullException.ThrowIfNull(contents);

		var listed = await mods.ListAsync(instance, ct).ConfigureAwait(false);
		if (!listed.IsSuccess)
		{
			return Result.Fail<ModPackPlanPayload>(listed.Failure!);
		}

		var configs = await mods.ListConfigsAsync(instance, ct).ConfigureAwait(false);
		if (!configs.IsSuccess)
		{
			return Result.Fail<ModPackPlanPayload>(configs.Failure!);
		}

		var present = configs.Value.Files.Select(file => file.Name).ToHashSet(StringComparer.Ordinal);
		var current = new Dictionary<ModConfigName, JsonNode?>();
		foreach (var name in contents.Configs.Keys.Where(name => present.Contains(name.Value)))
		{
			// Непрочитанный файл сервера — «изменится»: сборка его заменит.
			var read = await mods.ReadConfigAsync(instance, name, ct).ConfigureAwait(false);
			current[name] = read.IsSuccess ? read.Value.Config : null;
		}

		return ModPack.Plan(contents.Manifest, contents.Configs, listed.Value, current, game);
	}

	/// <summary>
	/// Записать выбранные настройки и переключить моды. Каждый шаг самостоятелен: отказ одного не
	/// отменяет остальные, а попадает в ответ.
	/// </summary>
	public static async Task<ModPackApplyPayload> ApplyAsync(
		IModStore mods,
		StagingWriter staging,
		InstanceId instance,
		ModPackContents contents,
		IReadOnlyList<ModConfigName> configs,
		IReadOnlyList<ModArchiveName> toggles,
		CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(mods);
		ArgumentNullException.ThrowIfNull(staging);
		ArgumentNullException.ThrowIfNull(contents);
		ArgumentNullException.ThrowIfNull(configs);
		ArgumentNullException.ThrowIfNull(toggles);

		var written = new List<string>();
		var toggled = new List<string>();
		var failures = new List<ModPackFailurePayload>();

		foreach (var name in configs)
		{
			if (!contents.Files.TryGetValue(name, out var bytes))
			{
				continue;
			}

			var staged = StagingWriter.Reserve(StagedFile.Purpose.Config, ".json");
			try
			{
				Result<Done> saved;
				await using (var stream = new MemoryStream(bytes, writable: false))
				{
					var stored = await staging.WriteAsync(stream, staged, 8, ct).ConfigureAwait(false);
					saved = stored.IsSuccess
						? await mods.WriteConfigAsync(instance, name, staged, ct).ConfigureAwait(false)
						: Result.Fail<Done>(stored.Failure!);
				}

				if (saved.IsSuccess)
				{
					written.Add(name.Value);
				}
				else
				{
					failures.Add(new ModPackFailurePayload { Name = name.Value, Error = saved.Failure!.Text });
				}
			}
			finally
			{
				staging.Discard(staged);
			}
		}

		foreach (var archive in toggles)
		{
			var result = await mods.ToggleAsync(instance, archive, ct).ConfigureAwait(false);
			if (result.IsSuccess)
			{
				toggled.Add(archive.Value);
			}
			else
			{
				failures.Add(new ModPackFailurePayload { Name = archive.Value, Error = result.Failure!.Text });
			}
		}

		return new ModPackApplyPayload
		{
			Ok = failures.Count == 0,
			Configs = written,
			Toggled = toggled,
			Failures = failures,
			RestartRequired = written.Count > 0 || toggled.Count > 0,
		};
	}

	private static Result<ModPackContents> Read(ZipArchive zip)
	{
		if (zip.Entries.Count > ModPack.MaximumEntries)
		{
			return Failure.Invalid($"В сборке больше {ModPack.MaximumEntries} записей");
		}

		// Суждение обо всём архиве до чтения первого байта содержимого.
		var judged = new List<(ZipArchiveEntry Entry, ModPackEntry Kind)>();
		var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var total = 0L;
		foreach (var entry in zip.Entries)
		{
			var kind = ModPack.JudgeEntry(Describe(entry));
			if (!kind.IsSuccess)
			{
				return Result.Fail<ModPackContents>(kind.Failure!);
			}

			if (!names.Add(entry.FullName.TrimEnd('/')))
			{
				return Failure.Invalid($"Имена в сборке совпадают без учёта регистра: {entry.FullName}");
			}

			if (kind.Value.Kind != ModPackEntryKind.Directory)
			{
				var counted = ModPack.CountBytes(total, entry.Length, kind.Value.Kind);
				if (!counted.IsSuccess)
				{
					return Result.Fail<ModPackContents>(counted.Failure!);
				}

				total = counted.Value;
			}

			judged.Add((entry, kind.Value));
		}

		byte[]? manifestBytes = null;
		var files = new Dictionary<ModConfigName, byte[]>();
		var configs = new Dictionary<ModConfigName, JsonNode>();
		foreach (var (entry, kind) in judged)
		{
			if (kind.Kind == ModPackEntryKind.Directory)
			{
				continue;
			}

			var bytes = ReadBounded(entry, kind.Kind == ModPackEntryKind.Manifest ? ModPack.MaximumManifestBytes : ModPack.MaximumConfigBytes);
			if (bytes is null)
			{
				return Failure.Invalid($"Запись сборки больше объявленного размера: {entry.FullName}");
			}

			if (kind.Kind == ModPackEntryKind.Manifest)
			{
				manifestBytes = bytes;
				continue;
			}

			var parsed = Parse(bytes);
			if (parsed is not (JsonObject or JsonArray))
			{
				return Failure.Invalid($"Файл настроек {kind.Config.Value} в сборке не является объектом или массивом JSON");
			}

			files[kind.Config] = bytes;
			configs[kind.Config] = parsed;
		}

		if (manifestBytes is null)
		{
			return Failure.Invalid("В сборке нет modpack.json");
		}

		var manifest = ModPack.FromJson(Parse(manifestBytes));
		if (!manifest.IsSuccess)
		{
			return Result.Fail<ModPackContents>(manifest.Failure!);
		}

		var listed = manifest.Value.Configs.ToHashSet(StringComparer.Ordinal);
		var missing = listed.FirstOrDefault(name => !files.Keys.Any(file => file.Value == name));
		if (missing is not null)
		{
			return Failure.Invalid($"Файла настроек {missing} из описи нет в сборке");
		}

		var unlisted = files.Keys.FirstOrDefault(file => !listed.Contains(file.Value));
		if (!unlisted.IsEmpty)
		{
			return Failure.Invalid($"Файла настроек {unlisted.Value} нет в описи сборки");
		}

		return new ModPackContents(manifest.Value, configs, files);
	}

	/// <summary>Вид записи ZIP: каталог по косой черте в конце, ссылка по битам режима Unix.</summary>
	private static ArchiveEntry Describe(ZipArchiveEntry entry)
	{
		const int typeMask = 0xF000;
		const int symbolicLink = 0xA000;
		const int regularFile = 0x8000;
		const int directory = 0x4000;

		var mode = (entry.ExternalAttributes >> 16) & 0xFFFF;
		var type = mode & typeMask;
		if (entry.FullName.EndsWith('/') || type == directory)
		{
			return ArchiveEntry.CreateDirectory(entry.FullName);
		}

		if (type == symbolicLink)
		{
			return ArchiveEntry.CreateSymbolicLink(entry.FullName, entry.FullName);
		}

		return type is 0 or regularFile
			? ArchiveEntry.CreateFile(entry.FullName)
			: new ArchiveEntry(entry.FullName, ArchiveEntryKind.Other, string.Empty, mode);
	}

	/// <summary>Содержимое записи или <c>null</c>, если оно длиннее предела.</summary>
	private static byte[]? ReadBounded(ZipArchiveEntry entry, long limit)
	{
		using var stream = entry.Open();
		using var buffer = new MemoryStream();
		var chunk = new byte[81920];
		int read;
		while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
		{
			if (buffer.Length + read > limit)
			{
				return null;
			}

			buffer.Write(chunk, 0, read);
		}

		return buffer.ToArray();
	}

	/// <summary>Разбор с теми же правилами, что у записи настроек в демоне: без комментариев и лишних запятых.</summary>
	private static JsonNode? Parse(byte[] bytes)
	{
		try
		{
			return JsonNode.Parse(bytes);
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static void Add(ZipArchive zip, string name, byte[] bytes)
	{
		var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
		using var stream = entry.Open();
		stream.Write(bytes);
	}
}
