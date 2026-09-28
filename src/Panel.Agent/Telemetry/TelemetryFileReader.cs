using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Contracts;
using Panel.Contracts.Agent;
using Panel.SharedKernel;

namespace Panel.Agent.Telemetry;

/// <summary>
/// Чтение telemetry.json, который мод атомарно переписывает каждые две секунды.
/// </summary>
public sealed class TelemetryFileReader
{
	private const long maxBytes = 1024 * 1024;

	/// <summary>Порог устаревания снимка.</summary>
	public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);

	private readonly TimeProvider time;

	public TelemetryFileReader(TimeProvider? time = null) => this.time = time ?? TimeProvider.System;

	public async Task<TelemetryPayload> ReadAsync(string path, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return Unavailable(Phrase.From("Для этого сервера не настроен файл телеметрии"));
		}

		FileInfo info;
		string json;
		try
		{
			info = new FileInfo(path);
			if (!info.Exists)
			{
				return Unavailable(Phrase.From("Установите и запустите мод VS Panel Telemetry"));
			}

			if (info.LinkTarget is not null || info.Length > maxBytes)
			{
				return Unavailable(Phrase.From("Telemetry file is invalid"));
			}

			json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
		}
		catch (FileNotFoundException)
		{
			return Unavailable(Phrase.From("Установите и запустите мод VS Panel Telemetry"));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Unavailable(Phrase.Format($"Cannot read telemetry: {exception.Message}"));
		}

		JsonNode? node;
		try
		{
			node = JsonNode.Parse(json);
		}
		catch (JsonException exception)
		{
			return Unavailable(Phrase.Format($"Cannot read telemetry: {exception.Message}"));
		}

		if (node is not JsonObject root || root["players"] is not (null or JsonArray))
		{
			return Unavailable(Phrase.From("Telemetry has an unsupported format"));
		}

		// Снимок разбирается целиком; нетипизированные поля попадают в Extra и передаются дальше
		// без изменений.
		TelemetryPayload? snapshot;
		try
		{
			snapshot = root.Deserialize<TelemetryPayload>(PanelJson.Options);
		}
		catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
		{
			return Unavailable(Phrase.From("Telemetry has an unsupported format"));
		}

		if (snapshot is null)
		{
			return Unavailable(Phrase.From("Telemetry has an unsupported format"));
		}

		var generatedAt = snapshot.GeneratedAt ?? 0;
		var now = time.GetUtcNow().ToUnixTimeSeconds();
		var age = Math.Max(0, now - (long) generatedAt);
		var stale = age > (long) StaleAfter.TotalSeconds;

		return snapshot with
		{
			Available = true,
			Stale = stale,
			AgeSeconds = age,
			Detail = stale ? Phrase.From("Телеметрия устарела: сервер или мод не отвечает") : snapshot.Detail,
		};
	}

	private static TelemetryPayload Unavailable(Phrase detail) => new()
	{
		Available = false,
		Stale = true,
		Detail = detail,
		Players = [],
	};
}
