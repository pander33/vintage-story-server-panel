using System.Text.Json;
using System.Text.Json.Nodes;

namespace Panel.Cli;

/// <summary>
/// Идёт ли в панели долгая операция, поверх которой нельзя копировать новую версию.
/// </summary>
/// <remarks>
/// <para>
/// Остановка панели не прерывает операцию обновления, и перезапуск поверх неё ждал бы до
/// <c>TimeoutStopSec</c>. Долгая операция владеет файлами (§2 заметки).
/// </para>
/// <para>
/// Признаков два: аренда обслуживания (<c>maintenance-lease.json</c>), которую держит любая
/// долгая операция, и запись <c>current</c> в <c>updates.json</c> каждого сервера и в
/// <c>provision.json</c>.
/// </para>
/// </remarks>
internal static class MaintenanceProbe
{
	/// <summary>Описание идущей операции или <c>null</c>, если панель свободна.</summary>
	public static string? Describe(string stateDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
		if (Read(Path.Combine(stateDirectory, "maintenance-lease.json")) is { } lease)
		{
			var purpose = Text(lease["purpose"]);
			var instance = Text(lease["instance"]);
			return $"обслуживание ({(purpose.Length > 0 ? purpose : "операция")}{(instance.Length > 0 ? $", сервер {instance}" : string.Empty)})";
		}

		var records = new List<string> { Path.Combine(stateDirectory, "updates.json"), Path.Combine(stateDirectory, "provision.json") };
		var instances = Path.Combine(stateDirectory, "instances");
		if (Directory.Exists(instances))
		{
			records.AddRange(Directory.GetDirectories(instances).Order(StringComparer.Ordinal).Select(dir => Path.Combine(dir, "updates.json")));
		}

		foreach (var path in records)
		{
			if (Read(path)?["current"] is JsonObject current)
			{
				var title = Text(current["title"]) is { Length: > 0 } named ? named : "операция обновления";
				var phase = Text(current["phase"]) is { Length: > 0 } stage ? stage
					: Text(current["status"]) is { Length: > 0 } status ? status
					: "выполняется";
				return $"{title} (этап: {phase})";
			}
		}

		return null;
	}

	private static JsonObject? Read(string path)
	{
		try
		{
			return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return null;
		}
	}

	private static string Text(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;
}
