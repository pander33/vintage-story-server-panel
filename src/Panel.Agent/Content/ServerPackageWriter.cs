using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Configuration.Domain;
using Panel.Content.Domain;
using Panel.Fleet.Domain;
using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Content;

/// <summary>Пишет переносимый снимок остановленного сервера из явно разрешённых файлов.</summary>
public static class ServerPackageWriter
{
	private static readonly HashSet<string> portableSettings = ConfigSchema.Fields
		.Where(field => field.Kind is ConfigFieldKind.Boolean or ConfigFieldKind.Integer or ConfigFieldKind.Number or ConfigFieldKind.Select)
		.Select(field => field.Key).ToHashSet(StringComparer.Ordinal);

	/// <summary>Записать tar.gz с манифестом, настройками, активным миром и файлами модов остановленного сервера.</summary>
	public static async Task<Result<int>> WriteAsync(
		RegisteredServer server, string archive, CancellationToken ct, OperationScope? scope = null)
	{
		ArgumentNullException.ThrowIfNull(server);
		var entries = new List<(string Source, string Name, long Length)>();
		try
		{
			ct.ThrowIfCancellationRequested();
			var data = new DirectoryInfo(server.DataDirectory);
			var saves = new DirectoryInfo(server.WorldDirectory);
			var mods = new DirectoryInfo(server.ModsDirectory);
			var configFile = new FileInfo(server.ConfigFile);
			if (!data.Exists || !SamePath(Path.Combine(data.FullName, "Saves"), saves.FullName)
				|| !SamePath(Path.Combine(data.FullName, "Mods"), mods.FullName)
				|| !SamePath(Path.Combine(data.FullName, "serverconfig.json"), configFile.FullName)
				|| !Plain(data) || !Plain(saves) || mods.Exists && !Plain(mods) || !Plain(configFile))
			{
				return Failure.Invalid("Каталоги и настройки сервера должны быть обычными файлами внутри данных");
			}

			var raw = await File.ReadAllBytesAsync(configFile.FullName, ct).ConfigureAwait(false);
			if (raw.Length > ServerTransferArchive.MaximumConfigBytes || JsonNode.Parse(raw) is not JsonObject config)
			{
				return Failure.Invalid("Настройки сервера отсутствуют или слишком велики");
			}

			var portable = ServerConfigPaths.Create(server.WorldDirectory, server.ModsDirectory).Portable(config);
			if (!portable.IsSuccess)
			{
				return Result.Fail<int>(portable.Failure!);
			}

			var settings = new JsonObject();
			foreach (var (key, value) in portable.Value)
			{
				if (portableSettings.Contains(key) && value is JsonValue scalar
					&& scalar.GetValueKind() is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number)
				{
					settings[key] = value?.DeepClone();
				}
			}

			if (portable.Value["WorldConfig"] is not JsonObject world
				|| world["SaveFileLocation"] is not JsonValue location
				|| !location.TryGetValue<string>(out var worldPath)
				|| !worldPath.StartsWith("Saves/", StringComparison.Ordinal))
			{
				return Failure.Invalid("В настройках сервера не задан активный мир");
			}

			var worldName = worldPath["Saves/".Length..];
			var worldEntry = "data/Saves/" + worldName;
			var worldJudged = ServerTransferArchive.JudgeEntry(ArchiveEntry.CreateFile(worldEntry));
			if (!worldJudged.IsSuccess || worldName.Contains('/'))
			{
				return Failure.Invalid("Недопустимое имя активного мира");
			}

			settings["WorldConfig"] = new JsonObject { ["SaveFileLocation"] = worldPath };
			settings["ModPaths"] = new JsonArray("Mods");
			var configBytes = Encoding.UTF8.GetBytes(settings.ToJsonString());
			if (configBytes.Length > ServerTransferArchive.MaximumConfigBytes)
			{
				return Failure.Invalid("Настройки сервера в пакете слишком велики");
			}

			var worldFile = new FileInfo(Path.Combine(saves.FullName, worldName));
			if (!Plain(worldFile) || worldFile.Length == 0)
			{
				return Failure.Invalid("Активный мир отсутствует или пуст");
			}
			entries.Add((worldFile.FullName, worldEntry, worldFile.Length));
			// Содержимое каталога модов не проверяется: берутся только файлы модов, всё остальное
			// (.history, каталоги, ссылки, чужие файлы) в пакет не попадает.
			foreach (var file in mods.Exists ? mods.EnumerateFiles() : [])
			{
				if (!Plain(file) || !file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
					&& !file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				var name = "data/Mods/" + file.Name;
				var judged = ServerTransferArchive.JudgeEntry(ArchiveEntry.CreateFile(name));
				if (!judged.IsSuccess)
				{
					return Result.Fail<int>(judged.Failure!);
				}
				entries.Add((file.FullName, name, file.Length));
			}

			var descriptions = new List<ArchiveEntry> { ArchiveEntry.CreateFile("data/serverconfig.json") };
			descriptions.AddRange(entries.Select(entry => ArchiveEntry.CreateFile(entry.Name)));
			var safe = ServerTransferArchive.Judge(descriptions);
			if (!safe.IsSuccess)
			{
				return Result.Fail<int>(safe.Failure!);
			}
			long total = configBytes.Length;
			foreach (var entry in entries)
			{
				var counted = ServerTransferArchive.CountBytes(total, entry.Length);
				if (!counted.IsSuccess)
				{
					return Result.Fail<int>(counted.Failure!);
				}
				total = counted.Value;
			}
			var transferFiles = new List<TransferFile>
			{
				new("data/serverconfig.json", configBytes.Length, Convert.ToHexStringLower(SHA256.HashData(configBytes))),
			};
			scope?.Enter(OperationStages.Checksums);
			scope?.Measure(entries.Sum(entry => entry.Length));
			foreach (var entry in entries)
			{
				await using var input = new FileStream(entry.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
				if (!Plain(new FileInfo(entry.Source)) || input.Length != entry.Length)
				{
					return Failure.Invalid("Исходный файл изменился во время создания пакета");
				}
				transferFiles.Add(new TransferFile(entry.Name, entry.Length,
					Convert.ToHexStringLower(await SHA256.HashDataAsync(new ProgressStream(input, scope), ct).ConfigureAwait(false))));
			}
			var manifest = ServerTransferManifest.Create(worldEntry, transferFiles, GameVersionOf(server));
			if (!manifest.IsSuccess)
			{
				return Result.Fail<int>(manifest.Failure!);
			}
			var manifestBytes = manifest.Value.Serialize();

			scope?.Enter(OperationStages.Pack);
			scope?.Measure(entries.Sum(entry => entry.Length));
			var created = false;
			try
			{
				{
					await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None);
					created = true;
					await using var gzip = new GZipStream(output, CompressionLevel.Optimal);
					await using var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);
					await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, ServerTransferArchive.Manifest)
					{
						DataStream = new MemoryStream(manifestBytes),
						Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
					}, ct).ConfigureAwait(false);
					await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "data/serverconfig.json")
					{
						DataStream = new MemoryStream(configBytes),
						Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
					}, ct).ConfigureAwait(false);
					foreach (var entry in entries.OrderBy(item => item.Name, StringComparer.Ordinal))
					{
						ct.ThrowIfCancellationRequested();
						var source = new FileInfo(entry.Source);
						if (!Plain(source) || source.Length != entry.Length)
						{
							throw new IOException("Исходный файл изменился во время создания пакета");
						}
						await using var input = new FileStream(entry.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
						await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, entry.Name)
						{
							DataStream = new ProgressStream(input, scope),
							Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
						}, ct).ConfigureAwait(false);
						await using var check = new FileStream(entry.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
						var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false));
						if (check.Length != entry.Length || hash != transferFiles.Single(file => file.Path == entry.Name).Sha256)
						{
							throw new IOException("Исходный файл изменился во время создания пакета");
						}
					}
				}
				scope?.Enter(OperationStages.Check);
				var verified = await TreeArchive.JudgeTransferAsync(archive, ct).ConfigureAwait(false);
				if (!verified.IsSuccess)
				{
					File.Delete(archive);
					return Result.Fail<int>(verified.Failure!);
				}
				return entries.Count + 2;
			}
			catch
			{
				if (created)
				{
					File.Delete(archive);
				}
				throw;
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
		{
			return Failure.Invalid($"Не удалось создать пакет сервера: {exception.Message}");
		}
	}

	/// <summary>Версия игры из реестра; неподходящая манифесту версия считается неизвестной.</summary>
	private static string GameVersionOf(RegisteredServer server) =>
		server.GameVersion is { Length: > 0 and <= ServerTransferManifest.MaximumGameVersionLength } version
			&& version.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+' or '_')
			? version
			: string.Empty;

	private static bool SamePath(string left, string right) =>
		string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

	private static bool Plain(FileSystemInfo item) => item.Exists && item.LinkTarget is null
		&& (item.Attributes & FileAttributes.ReparsePoint) == 0
		&& (item.Attributes & FileAttributes.Device) == 0;
}
