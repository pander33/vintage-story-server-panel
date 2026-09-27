using Panel.Content.Domain;
using Panel.Contracts.Content;
using Panel.SharedKernel;

namespace Panel.Content.Ports;

/// <summary>Результат установки или отката мода.</summary>
/// <param name="File">Имя файла, под которым мод теперь лежит.</param>
/// <param name="Id">Идентификатор из <c>modinfo.json</c>.</param>
/// <param name="Version">Версия из <c>modinfo.json</c>.</param>
/// <param name="Archived">Имена версий, убранных в историю.</param>
public sealed record ModPlacement(ModArchiveName File, ModId Id, string Version, IReadOnlyList<string> Archived);

/// <summary>Результат перемещения мода в историю.</summary>
/// <param name="File">Удалённый файл.</param>
/// <param name="ArchivedAs">Имя в <c>Mods/.history</c>.</param>
public sealed record ModRemoval(ModArchiveName File, string ArchivedAs);

/// <summary>Сохранённые версии мода, доступные для отката.</summary>
public sealed record ModHistory(ModId Id, IReadOnlyList<ModArchiveName> Versions);

/// <summary>Данные для вкладки модов в доменном виде.</summary>
public sealed record ModOverview(
	ModCatalogue Catalogue,
	IReadOnlyList<ModHistory> History,
	IReadOnlyList<ModProfileName> Profiles);

/// <summary>Файл настроек мода и его содержимое.</summary>
public sealed record ModConfigDocument(ModConfigName Name, string Json, long ModifiedTicks);

/// <summary>Запись в перечне файлов настроек.</summary>
public sealed record ModConfigEntry(ModConfigName Name, long Size, DateTimeOffset Modified);

/// <summary>Параметры нового мира, которые записываются в <c>WorldConfig</c>.</summary>
/// <param name="File">Имя файла базы.</param>
/// <param name="DisplayName">Название мира в игре.</param>
/// <param name="Seed">Зерно генерации; пустое — выберет игра.</param>
/// <param name="Style">Стиль игры.</param>
public sealed record WorldPlan(WorldFileName File, string DisplayName, string Seed, PlayStyle Style);

/// <summary>Мир, выгруженный в промежуточное хранение для скачивания.</summary>
public sealed record WorldExport(WorldFileName Name, StagedFile Staged, long Size);

/// <summary>
/// Моды одного сервера.
/// </summary>
/// <remarks>
/// <para>
/// На вход порт принимает доменные значения, а возвращает published language. Имя, профиль и
/// подтверждение проверяются до обращения к привилегированной стороне, и типы это
/// гарантируют. Разбор каталога модов (дубликаты, недостающие зависимости, история версий)
/// выполняется там, где лежат файлы, то есть в демоне: <see cref="ModCatalogue"/> работает на
/// привилегированной стороне, а сюда приходит готовый ответ.
/// </para>
/// <para>
/// Если разбирать ответ в домен, чтобы тут же собрать из него тот же ответ, появится второе
/// место, где форма ответа может разойтись с замороженной (ADR-16).
/// </para>
/// </remarks>
public interface IModStore
{
	Task<Result<ModsPayload>> ListAsync(InstanceId instance, CancellationToken cancellationToken);

	Task<Result<ModChangePayload>> InstallAsync(
		InstanceId instance,
		StagedFile staged,
		CancellationToken cancellationToken);

	Task<Result<ModChangePayload>> ToggleAsync(
		InstanceId instance,
		ModArchiveName mod,
		CancellationToken cancellationToken);

	Task<Result<ModChangePayload>> DeleteAsync(
		InstanceId instance,
		ModArchiveName mod,
		CancellationToken cancellationToken);

	Task<Result<ModChangePayload>> RollbackAsync(
		InstanceId instance,
		ModId mod,
		ModArchiveName archived,
		CancellationToken cancellationToken);

	Task<Result<ModProfilePayload>> SaveProfileAsync(
		InstanceId instance,
		ModProfileName profile,
		CancellationToken cancellationToken);

	Task<Result<ModProfilePayload>> ApplyProfileAsync(
		InstanceId instance,
		ModProfileName profile,
		CancellationToken cancellationToken);

	Task<Result<Done>> DeleteProfileAsync(
		InstanceId instance,
		ModProfileName profile,
		CancellationToken cancellationToken);

	Task<Result<ModConfigsPayload>> ListConfigsAsync(InstanceId instance, CancellationToken cancellationToken);

	Task<Result<ModConfigPayload>> ReadConfigAsync(
		InstanceId instance,
		ModConfigName name,
		CancellationToken cancellationToken);

	Task<Result<Done>> WriteConfigAsync(
		InstanceId instance,
		ModConfigName name,
		StagedFile staged,
		CancellationToken cancellationToken);
}

/// <summary>Миры одного сервера.</summary>
public interface IWorldStore
{
	Task<Result<WorldsPayload>> ListAsync(InstanceId instance, CancellationToken cancellationToken);

	Task<Result<WorldChangePayload>> ImportAsync(
		InstanceId instance,
		StagedFile staged,
		WorldFileName name,
		CancellationToken cancellationToken);

	Task<Result<WorldExportPayload>> ExportAsync(
		InstanceId instance,
		WorldFileName name,
		StagedFile staged,
		CancellationToken cancellationToken);

	Task<Result<WorldChangePayload>> CloneAsync(
		InstanceId instance,
		WorldFileName source,
		WorldFileName target,
		CancellationToken cancellationToken);

	Task<Result<WorldChangePayload>> ActivateAsync(
		InstanceId instance,
		WorldFileName name,
		CancellationToken cancellationToken);

	Task<Result<WorldChangePayload>> CreateAsync(
		InstanceId instance,
		WorldPlan plan,
		CancellationToken cancellationToken);

	Task<Result<WorldChangePayload>> PutAwayAsync(
		InstanceId instance,
		WorldFileName name,
		WorldDisposition disposition,
		CancellationToken cancellationToken);

	Task<Result<IReadOnlyList<StoredWorldPayload>>> ListStoredAsync(
		InstanceId instance,
		CancellationToken cancellationToken);

	Task<Result<WorldChangePayload>> RestoreStoredAsync(
		InstanceId instance,
		WorldDisposition disposition,
		StoredWorldName stored,
		WorldFileName target,
		CancellationToken cancellationToken);

	Task<Result<WorldChangePayload>> DeleteStoredAsync(
		InstanceId instance,
		WorldDisposition disposition,
		StoredWorldName stored,
		CancellationToken cancellationToken);
}

/// <summary>Резервные копии одного сервера.</summary>
public interface IBackupStore
{
	Task<Result<WorldExportPayload>> ExportPackageAsync(
		InstanceId instance,
		StagedFile staged,
		CancellationToken cancellationToken);

	Task<Result<BackupsPayload>> ListAsync(InstanceId instance, CancellationToken cancellationToken);

	/// <summary>
	/// Снять копию. <paramref name="offline"/> различает две команды: обычная сама останавливает
	/// службу, обслуживающая требует уже остановленной.
	/// </summary>
	Task<Result<BackupChangePayload>> CreateAsync(
		InstanceId instance,
		bool offline,
		CancellationToken cancellationToken);

	Task<Result<BackupContentsPayload>> InspectAsync(
		InstanceId instance,
		BackupName name,
		CancellationToken cancellationToken);

	Task<Result<BackupChangePayload>> RestoreAsync(
		InstanceId instance,
		BackupName name,
		CancellationToken cancellationToken);

	Task<Result<BackupChangePayload>> DeleteAsync(
		InstanceId instance,
		BackupName name,
		CancellationToken cancellationToken);

	Task<Result<WorldChangePayload>> RestoreWorldAsync(
		InstanceId instance,
		BackupName backup,
		string world,
		WorldFileName target,
		CancellationToken cancellationToken);

	/// <summary>
	/// Предпросмотр или применение политики удержания.
	/// </summary>
	/// <remarks>
	/// Считается на привилегированной стороне по тому же перечню файлов, по которому потом
	/// удаляются копии. Иначе перечень мог бы измениться между расчётом и удалением.
	/// </remarks>
	Task<Result<RetentionPayload>> ApplyRetentionAsync(
		InstanceId instance,
		RetentionPolicy policy,
		bool apply,
		CancellationToken cancellationToken);
}

/// <summary>Ход идущей долгой операции сервера: копия, восстановление, выгрузка пакета.</summary>
public interface IOperationProgress
{
	Task<Result<OperationPayload>> CurrentAsync(InstanceId instance, CancellationToken cancellationToken);
}

/// <summary>
/// История «на всякий случай»: заменённые моды, копии настроек сервера и модов.
/// </summary>
public interface IHistoryStore
{
	Task<Result<HistoryPayload>> ListAsync(InstanceId instance, CancellationToken ct);

	Task<Result<HistoryDeletePayload>> DeleteAsync(InstanceId instance, HistoryStore store, HistoryKey key, CancellationToken ct);

	Task<Result<HistoryPrunePayload>> PruneAsync(InstanceId instance, HistoryStore store, HistoryKeep keep, CancellationToken ct);
}
