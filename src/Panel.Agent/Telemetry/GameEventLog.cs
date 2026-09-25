using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Contracts.Agent;

namespace Panel.Agent.Telemetry;

/// <summary>
/// Хвост <c>VsPanel/events.jsonl</c>, журнала игровых событий, который пишет мод.
/// </summary>
/// <remarks>
/// Мод только дописывает файл и при 512 КБ один раз переносит его в <c>events.jsonl.1</c>.
/// Поэтому читается окно в конце файла, а недостающие строки берутся из предыдущего файла.
/// Строка, которую не удалось разобрать, пропускается.
/// </remarks>
public sealed class GameEventLog
{
	/// <summary>Размер окна чтения.</summary>
	private const int window = 256 * 1024;

	private static readonly HashSet<string> kinds = new(StringComparer.Ordinal) { "chat", "join", "leave", "death", "respawn" };

	public async Task<GameEventsPayload> ReadAsync(string path, int count, TelemetryPayload telemetry, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(telemetry);

		var limit = Math.Clamp(count, 1, 500);
		var lines = await TailAsync(path, limit, ct).ConfigureAwait(false);
		if (lines.Count < limit)
		{
			var older = await TailAsync(path + ".1", limit - lines.Count, ct).ConfigureAwait(false);
			lines = [.. older, .. lines];
		}

		var events = new List<GameEventPayload>(lines.Count);
		foreach (var line in lines)
		{
			if (Parse(line) is { } item)
			{
				events.Add(item);
			}
		}

		return new GameEventsPayload
		{
			Events = events,

			// Старый мод журнал не пишет; интерфейс должен сообщить об этом, а не показать пустую ленту.
			Supported = telemetry.EventsSupported == true || events.Count > 0,
		};
	}

	private static async Task<IReadOnlyList<string>> TailAsync(string path, int limit, CancellationToken ct)
	{
		if (limit <= 0 || string.IsNullOrEmpty(path))
		{
			return [];
		}

		try
		{
			var info = new FileInfo(path);
			if (!info.Exists || info.LinkTarget is not null)
			{
				return [];
			}

			var size = info.Length;
			var window = (int) Math.Min(size, GameEventLog.window);
			var buffer = new byte[window];

			await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
			{
				file.Seek(size - window, SeekOrigin.Begin);
				await file.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
			}

			var lines = new UTF8Encoding(false, false).GetString(buffer)
				.Split('\n')
				.Select(line => line.TrimEnd('\r'))
				.ToList();

			if (lines.Count > 0 && lines[^1].Length == 0)
			{
				lines.RemoveAt(lines.Count - 1);
			}

			// Окно начинается посреди файла, и первая строка обрезана.
			if (window < size && lines.Count > 0)
			{
				lines.RemoveAt(0);
			}

			return lines.Count > limit ? lines[^limit..] : lines;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}

	private static GameEventPayload? Parse(string line)
	{
		JsonNode? node;
		try
		{
			node = JsonNode.Parse(line);
		}
		catch (JsonException)
		{
			return null;
		}

		if (node is not JsonObject item || Text(item["kind"]) is not { } kind || !kinds.Contains(kind))
		{
			return null;
		}

		return new GameEventPayload
		{
			At = Integer(item["at"]) ?? 0,
			Seq = Integer(item["seq"]) ?? 0,
			Kind = kind,
			Player = Clip(Text(item["player"]), 64),
			Uid = Clip(Text(item["uid"]), 64),
			Channel = item["channel"] is null ? null : Integer(item["channel"]) ?? 0,
			Text = Clip(Text(item["text"]), 400),
		};
	}

	/// <summary>Число, числовая строка или логическое значение, иначе null.</summary>
	private static long? Integer(JsonNode? node)
	{
		if (node is not JsonValue value)
		{
			return null;
		}

		return value.GetValueKind() switch
		{
			JsonValueKind.Number when value.TryGetValue<long>(out var whole) => whole,
			JsonValueKind.Number when value.TryGetValue<double>(out var real) && double.IsFinite(real) => (long) real,
			JsonValueKind.String when long.TryParse(value.GetValue<string>().Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) => parsed,
			JsonValueKind.True => 1,
			JsonValueKind.False => 0,
			_ => null,
		};
	}

	private static string? Text(JsonNode? node) => node switch
	{
		null => null,
		JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
		_ => node.ToJsonString(),
	};

	private static string Clip(string? value, int maximum) =>
		value is null ? string.Empty : value.Length > maximum ? value[..maximum] : value;
}
