using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.SharedKernel;

namespace Panel.Agent.Supervision;

/// <summary>
/// Что демон помнит о запущенном сервере: <c>&lt;run_dir&gt;/&lt;id&gt;.json</c>.
/// </summary>
/// <remarks>
/// Ключи (<c>pid</c>, <c>created</c>, <c>started_at</c>, <c>log</c>, <c>restarts</c>) заморожены
/// (ADR-04): служба после обновления должна найти сервер, запущенный прежней версией.
///
/// <c>created</c> — момент создания процесса в секундах Unix. Windows переиспользует pid,
/// поэтому процесс считается своим только при совпадении обоих чисел.
/// </remarks>
public sealed record SupervisorState
{
	/// <summary>Расхождение времени создания, в пределах которого процесс считается тем же.</summary>
	public static readonly TimeSpan CreationTolerance = TimeSpan.FromSeconds(2);

	public int Pid { get; init; }

	public double Created { get; init; }

	public double StartedAt { get; init; }

	public string Log { get; init; } = string.Empty;

	public int Restarts { get; init; }

	/// <summary>Остальные ключи файла; сохраняются при записи.</summary>
	public JsonObject? Extra { get; init; }

	/// <summary>Тот ли это процесс: pid и момент создания совпадают.</summary>
	public bool IsSameProcess(double actualCreated) =>
		Pid > 0 && actualCreated > 0 && Math.Abs(actualCreated - Created) < CreationTolerance.TotalSeconds;

	public static string PathFor(string runDirectory, string instance) =>
		Path.Combine(runDirectory, instance + ".json");

	/// <summary>Нечитаемый или отсутствующий файл даёт пустое состояние.</summary>
	public static SupervisorState Read(string path)
	{
		JsonObject? document;
		try
		{
			document = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return new SupervisorState();
		}

		if (document is null)
		{
			return new SupervisorState();
		}

		var extra = (JsonObject) document.DeepClone();
		foreach (var key in keys)
		{
			extra.Remove(key);
		}

		return new SupervisorState
		{
			Pid = (int) Number(document["pid"]),
			Created = Number(document["created"]),
			StartedAt = Number(document["started_at"]),
			Log = document["log"] is JsonValue log && log.GetValueKind() == JsonValueKind.String ? log.GetValue<string>() : string.Empty,
			Restarts = (int) Number(document["restarts"]),
			Extra = extra.Count > 0 ? extra : null,
		};
	}

	public async Task<Result<Done>> WriteAsync(string path, CancellationToken ct)
	{
		var document = Extra is null ? new JsonObject() : (JsonObject) Extra.DeepClone();
		document["pid"] = Pid;
		document["created"] = Created;
		document["started_at"] = StartedAt;
		document["log"] = Log;
		document["restarts"] = Restarts;

		return await AtomicFile
			.WriteAsync(path, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct)
			.ConfigureAwait(false);
	}

	private static readonly string[] keys = ["pid", "created", "started_at", "log", "restarts"];

	/// <summary>
	/// Число из JSON: <c>pid</c> целое, <c>created</c> дробное, но старая запись могла оставить
	/// строку. Непонятное значение — ноль, то есть неизвестно.
	/// </summary>
	private static double Number(JsonNode? node)
	{
		if (node is not JsonValue value)
		{
			return 0;
		}

		return value.GetValueKind() switch
		{
			JsonValueKind.Number => value.GetValue<double>(),
			JsonValueKind.String when double.TryParse(value.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
			_ => 0,
		};
	}
}
