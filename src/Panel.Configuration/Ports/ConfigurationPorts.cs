using Panel.Configuration.Domain;
using Panel.Contracts;
using Panel.Contracts.Configuration;
using Panel.SharedKernel;

namespace Panel.Configuration.Ports;

/// <summary>
/// <c>serverconfig.json</c> сервера. Файл лежит в игровом каталоге, читает и пишет его
/// привилегированная сторона.
/// </summary>
/// <remarks>
/// Как и порты содержимого, на вход принимает доменные значения, а возвращает published
/// language. В агрегат документ разбирает сам контекст, потому что ревизия и дифф считаются
/// в веб-процессе.
/// </remarks>
public interface IServerConfigStore
{
	Task<Result<ServerConfigFilePayload>> ReadAsync(InstanceId instance, CancellationToken ct);

	/// <summary>
	/// Заменить файл содержимым из staging. Демон снимает копию прежнего файла, пишет атомарно и
	/// отдаёт файл игровому пользователю.
	/// </summary>
	Task<Result<ConfigWritePayload>> WriteAsync(InstanceId instance, StagedFile staged, CancellationToken ct);

	/// <summary>Не более пятидесяти последних копий, новые первыми.</summary>
	Task<Result<ConfigBackupsPayload>> ListBackupsAsync(InstanceId instance, CancellationToken ct);

	/// <summary>Вернуть копию на место, сняв копию текущего файла.</summary>
	Task<Result<ConfigRollbackPayload>> RollbackAsync(InstanceId instance, ConfigBackupName backup, CancellationToken ct);
}

/// <summary>
/// Схема параметров мира: стили игры и параметры, объявленные игрой и включёнными модами. Читает
/// демон — он знает каталог программы и модов сервера.
/// </summary>
public interface IWorldSchemaStore
{
	/// <summary>
	/// Подписи — из языковых файлов игры и модов на языке <paramref name="language"/>: их переводит
	/// игра, а не каталог панели.
	/// </summary>
	Task<Result<WorldSchemaPayload>> ReadAsync(InstanceId instance, PanelLanguage language, CancellationToken ct);
}

/// <summary>
/// Хранилище отметки трекера: <c>instances/&lt;id&gt;/config-apply.json</c> в каталоге
/// состояния панели. Файл принадлежит веб-процессу.
/// </summary>
public interface IApplyMarkerStore
{
	/// <summary>Отметка сервера или <c>null</c>, если её нет, она слишком велика или повреждена.</summary>
	Task<ApplyMarker?> ReadAsync(InstanceId instance, CancellationToken ct);

	Task<Result<Done>> WriteAsync(InstanceId instance, ApplyMarker marker, CancellationToken ct);
}
