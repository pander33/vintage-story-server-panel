using System.Text.Json.Nodes;
using Panel.Content.Domain;
using Panel.Content.Ports;
using Panel.Contracts.Content;
using Panel.SharedKernel;

namespace Panel.Agent.Transport;

/// <summary>
/// Перевод доменных типов содержимого в форму ответа.
/// </summary>
/// <remarks>
/// Отдельно от доменных типов: форма ответа заморожена (ADR-16), а домен должен меняться
/// свободно.
/// </remarks>
internal static class ContentMapping
{
	/// <summary>Время в ответах — секунды эпохи, как <c>int(st_mtime)</c>.</summary>
	private static long Seconds(DateTimeOffset moment) => moment.ToUnixTimeSeconds();

	public static ModsPayload ToPayload(ModOverview overview)
	{
		var catalogue = overview.Catalogue;

		return new ModsPayload
		{
			Mods =
			[
				.. catalogue.Mods.Select(mod => new ModEntryPayload
				{
					Name = mod.File.Value,
					Enabled = mod.Enabled,
					Size = mod.Size,
					Modified = Seconds(mod.Modified),
					ModId = mod.Id.IsEmpty ? null : mod.Id.Value,
					Version = mod.Unreadable ? null : mod.Version,
					DisplayName = mod.DisplayName,
					Side = mod.Unreadable ? null : mod.Side,
					Dependencies = mod.Dependencies,
					MissingDependencies = catalogue.MissingDependenciesOf(mod),
					Error = mod.Unreadable ? Phrase.From("Некорректный ZIP или modinfo.json") : null,
				}),
			],
			Enabled = catalogue.EnabledCount,
			DuplicateIds = [.. catalogue.DuplicateIds.Select(id => id.Value)],
			Rollbacks = overview.History.ToDictionary(
				item => item.Id.Value,
				item => (IReadOnlyList<string>) [.. item.Versions.Select(version => version.Value)],
				StringComparer.Ordinal),
			Profiles = [.. overview.Profiles.Select(profile => profile.Value)],
		};
	}

	public static ModChangePayload ToPayload(ModPlacement placement) => new()
	{
		Name = placement.File.Value,
		ModId = placement.Id.Value,
		Version = placement.Version,
		Archived = placement.Archived,
	};

	public static ModChangePayload ToPayload(ModRemoval removal) => new()
	{
		Name = removal.File.Value,
		Archived = [removal.ArchivedAs],
	};

	public static ModChangePayload ToPayload(InstalledMod mod) => new()
	{
		Name = mod.File.Value,
		ModId = mod.Id.IsEmpty ? null : mod.Id.Value,
		Version = mod.Unreadable ? null : mod.Version,
		Enabled = mod.Enabled,
	};

	public static ModProfilePayload ToPayload(ModProfile profile) => new()
	{
		Profile = profile.Name.Value,
		Mods = [.. profile.Files.Select(file => file.Value)],
	};

	public static ModConfigsPayload ToPayload(IReadOnlyList<ModConfigEntry> entries) => new()
	{
		Files =
		[
			.. entries.Select(entry => new ModConfigEntryPayload
			{
				Name = entry.Name.Value,
				Size = entry.Size,
				Modified = Seconds(entry.Modified),
			}),
		],
	};

	public static ModConfigPayload ToPayload(ModConfigDocument document) => new()
	{
		Name = document.Name.Value,
		Config = JsonNode.Parse(document.Json),
		ModifiedNanoseconds = document.ModifiedTicks * 100,
	};

	public static WorldsPayload ToPayload(WorldCollection collection) => new()
	{
		Worlds =
		[
			.. collection.Worlds.Select(world => new WorldEntryPayload
			{
				Name = world.Name.Value,
				Size = world.Size,
				Modified = Seconds(world.Modified),
				Active = world.Active,
			}),
		],
		Active = collection.Active?.Name.Value,
		ServerActive = collection.ServerRunning,
	};

	public static IReadOnlyList<StoredWorldPayload> ToPayload(IReadOnlyList<StoredWorld> worlds) =>
	[
		.. worlds.Select(world => new StoredWorldPayload
		{
			StoredName = world.StoredName.Value,
			OriginalName = world.OriginalName,
			Mode = WorldDispositionWire.ToWire(world.Disposition),
			Size = world.Size,
			Modified = Seconds(world.Modified),
		}),
	];

	public static BackupsPayload ToPayload(BackupSet set) => new()
	{
		Backups =
		[
			.. set.Backups.Select(backup => new BackupEntryPayload
			{
				Name = backup.Name.Value,
				Size = backup.Size,
				Modified = Seconds(backup.Modified),
			}),
		],
	};

	public static BackupContentsPayload ToPayload(BackupName name, BackupContents contents) => new()
	{
		Name = name.Value,
		Files = contents.Files,
		UnpackedSize = contents.UnpackedSize,
		Worlds =
		[
			.. contents.Worlds.Select(world => new BackupWorldPayload
			{
				Name = world.Name,
				Member = world.Member,
				Size = world.Size,
			}),
		],
	};
}
