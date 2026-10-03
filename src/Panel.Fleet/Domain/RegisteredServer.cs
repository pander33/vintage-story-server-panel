using Panel.SharedKernel;

namespace Panel.Fleet.Domain;

/// <summary>
/// Запись реестра: сервер, о котором знает панель. Пути вывел привилегированный помощник
/// при создании сервера, панель их только читает.
/// </summary>
/// <remarks>
/// Хранится исходный JSON-узел, чтобы запись можно было сохранить обратно без потери
/// неизвестных полей.
/// </remarks>
public sealed record RegisteredServer
{
	public required InstanceId Id { get; init; }

	/// <summary>Отображаемое имя. Пустое в файле означает, что сервер называется по идентификатору.</summary>
	public required string Name { get; init; }

	/// <summary>Имя юнита systemd или службы Windows.</summary>
	public required string Unit { get; init; }

	public string DataDirectory { get; init; } = string.Empty;

	/// <summary>Каталог программы сервера; сюда распаковывается пакет игры при обновлении.</summary>
	public string ProgramDirectory { get; init; } = string.Empty;

	/// <summary>Каталог включённых модов. Отключённые лежат в его подкаталоге <c>.disabled</c>.</summary>
	public string ModsDirectory { get; init; } = string.Empty;

	/// <summary>Каталог миров; по умолчанию <c>Saves</c> внутри данных.</summary>
	public string WorldDirectory { get; init; } = string.Empty;

	/// <summary>Каталог резервных копий. Лежит вне данных, иначе копия включала бы сама себя.</summary>
	public string BackupDirectory { get; init; } = string.Empty;

	/// <summary>Каталог настроек модов, <c>ModConfig</c> внутри данных.</summary>
	public string ModConfigDirectory { get; init; } = string.Empty;

	/// <summary><c>serverconfig.json</c> сервера.</summary>
	public string ConfigFile { get; init; } = string.Empty;

	/// <summary>Каталог журналов игры.</summary>
	public string LogDirectory { get; init; } = string.Empty;

	public string TelemetryFile { get; init; } = string.Empty;

	public string GameEventsFile { get; init; } = string.Empty;

	/// <summary>Транспорт консоли: disabled | fifo | screen | server.sh.</summary>
	public string CommandTransport { get; init; } = "disabled";

	/// <summary>Именованный канал, созданный vs-console-wrapper; только для транспорта fifo.</summary>
	public string ConsoleFifo { get; init; } = string.Empty;

	/// <summary>Имя сессии screen; только для транспорта screen.</summary>
	public string ScreenSession { get; init; } = string.Empty;

	/// <summary>server.sh самой игры; только для одноимённого транспорта.</summary>
	public string CommandRunner { get; init; } = string.Empty;

	public string? GameVersion { get; init; }

	/// <summary>
	/// Запись целиком, как в файле, чтобы сохранить её обратно без потери неизвестных этой
	/// версии ключей.
	/// </summary>
	public System.Text.Json.Nodes.JsonObject? Raw { get; init; }
}
