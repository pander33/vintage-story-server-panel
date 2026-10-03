using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Configuration.Domain;
using Panel.Configuration.Ports;
using Panel.SharedKernel;

namespace Panel.Configuration.Infrastructure;

/// <summary>
/// <c>instances/&lt;id&gt;/config-apply.json</c>: отметка трекера применения.
/// </summary>
/// <remarks>
/// Отметка записывается через PythonJson с переводом строки в конце.
/// Ревизия настроек вычисляется отдельно через CanonicalJson.
/// </remarks>
public sealed class FileApplyMarkerStore : IApplyMarkerStore
{
	/// <summary>Предел размера отметки.</summary>
	private const long maxBytes = 64 * 1024;

	private const string fileName = "config-apply.json";

	private readonly string stateDirectory;

	public FileApplyMarkerStore(string stateDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
		this.stateDirectory = stateDirectory;
	}

	public async Task<ApplyMarker?> ReadAsync(InstanceId instance, CancellationToken ct)
	{
		var path = PathOf(instance);

		try
		{
			var info = new FileInfo(path);
			if (!info.Exists || info.Length > maxBytes)
			{
				return null;
			}

			var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
			return JsonNode.Parse(json) is JsonObject root ? Parse(root) : null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return null;
		}
	}

	public async Task<Result<Done>> WriteAsync(InstanceId instance, ApplyMarker marker, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(marker);

		var path = PathOf(instance);
		var directory = Path.GetDirectoryName(path)!;

		var document = new JsonObject
		{
			["revision"] = marker.Revision,
			["service_instance"] = marker.ServiceInstance,
			["changed_keys"] = new JsonArray([.. marker.ChangedKeys.Select(key => (JsonNode?) JsonValue.Create(key))]),
			["requires_restart"] = marker.RequiresRestart,
			["saved_at"] = marker.SavedAt,
		};

		try
		{
			if (!Directory.Exists(directory))
			{
				Directory.CreateDirectory(directory);
				if (!OperatingSystem.IsWindows())
				{
					File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Не удалось создать {directory}: {exception.Message}");
		}

		var written = await AtomicFile.WriteAsync(path, PythonJson.Spaced(document) + "\n", ct).ConfigureAwait(false);
		if (written.IsSuccess && !OperatingSystem.IsWindows())
		{
			// AtomicFile сохраняет права существующего файла, а новый сразу получает 0600, а не права
			// по umask.
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}

		return written;
	}

	/// <summary>
	/// Нестрогий разбор: поле неверного типа считается отсутствующим.
	/// </summary>
	private static ApplyMarker Parse(JsonObject root)
	{
		var keys = root["changed_keys"] is JsonArray array
			&& array.All(item => item is JsonValue value && value.GetValueKind() == JsonValueKind.String)
				? array.Select(item => item!.GetValue<string>()).ToList()
				: [];

		return new ApplyMarker(
			Text(root["revision"]),
			Text(root["service_instance"]),
			keys,
			root["requires_restart"] is JsonValue flag && flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False
				? flag.GetValue<bool>()
				: null,
			root["saved_at"] is JsonValue saved && saved.TryGetValue<long>(out var seconds) ? seconds : null);
	}

	private static string? Text(JsonNode? node) =>
		node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

	private string PathOf(InstanceId instance) =>
		Path.Combine(stateDirectory, "instances", instance.Value, fileName);
}
