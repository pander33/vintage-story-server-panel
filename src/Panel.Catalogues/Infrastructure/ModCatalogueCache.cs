using Panel.Catalogues.Domain;
using Panel.SharedKernel;

namespace Panel.Catalogues.Infrastructure;

/// <summary>Список модов, теги и подсказка о совместимости на момент ответа.</summary>
/// <param name="Compatible">Номера модов с релизом, отмеченным ровно этой версией игры; <c>null</c>, если узнать не удалось.</param>
/// <param name="Stale">Список старше срока: ModDB не ответил, и показан прежний.</param>
public sealed record ModCatalogueSnapshot(
	IReadOnlyList<CatalogueEntry> Entries,
	IReadOnlyList<CatalogueTag> Tags,
	IReadOnlySet<long>? Compatible,
	bool Stale);

/// <summary>
/// Общий список ModDB в памяти панели: один на панель, а не на сервер.
/// </summary>
/// <remarks>
/// Живёт час. Параллельные запросы ждут одну загрузку. Отказ ModDB при живом кэше — отдать прежний
/// список с пометкой «устарел»; без кэша — отказ. Совместимость с версией игры кэшируется отдельно
/// по названию версии и никогда не роняет весь ответ: подсказку можно и не показать.
/// </remarks>
public sealed class ModCatalogueCache
{
	public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

	/// <summary>
	/// Пауза после отказа ModDB. Без неё каждый запрос каталога при недоступном ModDB ждал бы
	/// срок ожидания загрузки, держа общую блокировку.
	/// </summary>
	public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

	private readonly ModDbClient moddb;
	private readonly TimeProvider clock;
	private readonly SemaphoreSlim gate = new(1, 1);
	private readonly Dictionary<string, (IReadOnlySet<long> Ids, long At)> compatible = new(StringComparer.Ordinal);
	private readonly Dictionary<string, long> compatibleFailed = new(StringComparer.Ordinal);
	private (IReadOnlyList<CatalogueEntry> Entries, IReadOnlyList<CatalogueTag> Tags, long At)? list;
	private (Failure Failure, long At)? listFailed;

	public ModCatalogueCache(ModDbClient moddb, TimeProvider clock)
	{
		ArgumentNullException.ThrowIfNull(moddb);
		ArgumentNullException.ThrowIfNull(clock);
		this.moddb = moddb;
		this.clock = clock;
	}

	public async Task<Result<ModCatalogueSnapshot>> SnapshotAsync(GameVersion game, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(game);
		await gate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			if ((list is null || Expired(list.Value.At)) && !(listFailed is { } failed && Recent(failed.At)))
			{
				var loaded = await moddb.ListCatalogueAsync(ct).ConfigureAwait(false);
				if (loaded.IsSuccess)
				{
					var tags = await moddb.ListTagsAsync(ct).ConfigureAwait(false);
					list = (loaded.Value, tags.IsSuccess ? tags.Value : list?.Tags ?? [], clock.GetTimestamp());
					listFailed = null;
				}
				else
				{
					listFailed = (loaded.Failure!, clock.GetTimestamp());
				}
			}

			if (list is not { } current)
			{
				return Result.Fail<ModCatalogueSnapshot>(listFailed!.Value.Failure);
			}

			var ids = await CompatibleAsync(game, ct).ConfigureAwait(false);
			return new ModCatalogueSnapshot(current.Entries, current.Tags, ids, Expired(current.At));
		}
		finally
		{
			gate.Release();
		}
	}

	private async Task<IReadOnlySet<long>?> CompatibleAsync(GameVersion game, CancellationToken ct)
	{
		var known = compatible.TryGetValue(game.Text, out var cached) ? cached : ((IReadOnlySet<long> Ids, long At)?) null;
		if (known is { } fresh && !Expired(fresh.At))
		{
			return fresh.Ids;
		}

		if (compatibleFailed.TryGetValue(game.Text, out var failedAt) && Recent(failedAt))
		{
			return known?.Ids;
		}

		var loaded = await moddb.ListCompatibleAsync(game, ct).ConfigureAwait(false);
		if (loaded.IsSuccess)
		{
			compatible[game.Text] = (loaded.Value, clock.GetTimestamp());
			compatibleFailed.Remove(game.Text);
			return loaded.Value;
		}

		compatibleFailed[game.Text] = clock.GetTimestamp();
		return known?.Ids;
	}

	private bool Expired(long at) => clock.GetElapsedTime(at) > Lifetime;

	private bool Recent(long at) => clock.GetElapsedTime(at) < RetryDelay;
}
