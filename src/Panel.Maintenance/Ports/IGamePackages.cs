using Panel.Contracts.Agent;
using Panel.Maintenance.Domain;
using Panel.SharedKernel;

namespace Panel.Maintenance.Ports;

/// <summary>
/// Программа сервера: проверка пакета, установка со снимком прежней версии, откат к снимку,
/// запись версии в реестр.
/// </summary>
/// <remarks>
/// <c>game-package-inspect</c> остаётся отдельной командой: проверка идёт до остановки
/// сервера и копии, чтобы негодный пакет не стоил игрокам перезапуска.
/// </remarks>
public interface IGamePackages
{
	Task<Result<GamePackageInspectionPayload>> InspectAsync(InstanceId instance, StagedFile package, CancellationToken ct);

	/// <summary>Установить пакет. Сервер должен быть остановлен; прежняя программа сохраняется снимком.</summary>
	Task<Result<GameUpdateAppliedPayload>> ApplyAsync(InstanceId instance, StagedFile package, CancellationToken ct);

	Task<Result<GameRollbackPayload>> RollbackAsync(InstanceId instance, SnapshotName snapshot, CancellationToken ct);

	Task<Result<GameVersionRecordedPayload>> RecordVersionAsync(InstanceId instance, string version, CancellationToken ct);

	Task<Result<ProgramSnapshotsPayload>> HistoryAsync(InstanceId instance, CancellationToken ct);
}
