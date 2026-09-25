using System.Text.RegularExpressions;
using Panel.Configuration.Domain;
using Panel.Content.Domain;
using Panel.Contracts.Content;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Content;

/// <summary>
/// Три хранилища истории от имени root: перечень, удаление записи, очистка.
/// </summary>
/// <remarks>
/// <para>
/// Ключ из запроса ищется в индексе, построенном здесь же, и сам в путь не превращается.
/// </para>
/// <para>
/// Каталоги истории модов отбираются правилом <c>SAFE_MOD_ID</c> (до 128 символов), а не более
/// строгим <see cref="ModId"/>, иначе уже накопленная история пропала бы из списка и её нельзя
/// было бы удалить.
/// </para>
/// </remarks>
public sealed partial class HistoryBackend
{
	public HistoryPayload List(RegisteredServer server)
	{
		var stores = new Dictionary<string, IReadOnlyList<HistoryItemPayload>>(StringComparer.Ordinal);
		var bytes = new Dictionary<string, long>(StringComparer.Ordinal);

		foreach (var store in HistoryStoreWire.All)
		{
			var items = HistoryRules.Ordered(Index(server, store).Values.Select(item => item.Entry))
				.Select(entry => new HistoryItemPayload
				{
					Key = entry.Key,
					Group = entry.Group,
					Name = entry.Name,
					Size = entry.Size,
					Modified = entry.Modified.ToUnixTimeSeconds(),
				})
				.ToList();

			var name = HistoryStoreWire.ToWire(store);
			stores[name] = items;
			bytes[name] = items.Sum(item => item.Size);
		}

		return new HistoryPayload { Stores = stores, Bytes = bytes };
	}

	public Result<HistoryDeletePayload> Delete(RegisteredServer server, HistoryStore store, HistoryKey key)
	{
		if (!Index(server, store).TryGetValue(key.Value, out var item))
		{
			return Failure.NotFound("Запись истории не найдена");
		}

		File.Delete(item.Path);
		return new HistoryDeletePayload
		{
			Store = HistoryStoreWire.ToWire(store),
			Key = key.Value,
			Freed = item.Entry.Size,
		};
	}

	public HistoryPrunePayload Prune(RegisteredServer server, HistoryStore store, HistoryKeep keep)
	{
		var index = Index(server, store);
		var removed = new List<string>();
		var freed = 0L;

		foreach (var entry in HistoryRules.Prune(index.Values.Select(item => item.Entry), keep))
		{
			File.Delete(index[entry.Key].Path);
			removed.Add(entry.Key);
			freed += entry.Size;
		}

		removed.Sort(StringComparer.Ordinal);
		return new HistoryPrunePayload
		{
			Store = HistoryStoreWire.ToWire(store),
			Keep = keep.Count,
			Removed = removed,
			Freed = freed,
		};
	}

	/// <summary>Ключ → файл и запись. Только обычные файлы, без ссылок.</summary>
	private static Dictionary<string, (string Path, HistoryEntry Entry)> Index(RegisteredServer server, HistoryStore store)
	{
		var index = new Dictionary<string, (string, HistoryEntry)>(StringComparer.Ordinal);

		switch (store)
		{
			case HistoryStore.ModHistory:
				{
					var root = new DirectoryInfo(Path.Combine(server.ModsDirectory, ".history"));
					if (!root.Exists)
					{
						break;
					}

					foreach (var directory in root.EnumerateDirectories().Where(item => item.LinkTarget is null))
					{
						if (!ModFolder().IsMatch(directory.Name))
						{
							continue;
						}

						foreach (var file in directory.EnumerateFiles("*.zip").Where(IsPlain))
						{
							Add(index, $"{directory.Name}/{file.Name}", directory.Name, file);
						}
					}

					break;
				}

			case HistoryStore.ConfigBackup:
				{
					var target = new FileInfo(server.ConfigFile);
					if (target.Directory is not { Exists: true } directory)
					{
						break;
					}

					foreach (var file in directory.EnumerateFiles(target.Name + ".panel-*.bak").Where(IsPlain))
					{
						if (ConfigBackupName.CreateFor(file.Name, target.Name).IsSuccess)
						{
							Add(index, file.Name, target.Name, file);
						}
					}

					break;
				}

			case HistoryStore.ModConfigBackup:
				{
					var directory = new DirectoryInfo(server.ModConfigDirectory);
					if (!directory.Exists)
					{
						break;
					}

					foreach (var file in directory.EnumerateFiles("*.bak").Where(IsPlain))
					{
						var match = ModConfigBackup().Match(file.Name);
						if (match.Success)
						{
							Add(index, file.Name, match.Groups["group"].Value, file);
						}
					}

					break;
				}
		}

		return index;
	}

	private static void Add(Dictionary<string, (string, HistoryEntry)> index, string key, string group, FileInfo file) =>
		index[key] = (file.FullName, new HistoryEntry(key, group, file.Name, file.Length, file.LastWriteTimeUtc));

	private static bool IsPlain(FileInfo file) => file.LinkTarget is null;

	[GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant)]
	private static partial Regex ModFolder();

	[GeneratedRegex(@"^(?<group>.+\.json)\.panel-\d{8}-\d{6}\.bak\z", RegexOptions.CultureInvariant)]
	private static partial Regex ModConfigBackup();
}
