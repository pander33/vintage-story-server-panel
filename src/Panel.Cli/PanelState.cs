using System.Text.Json;
using System.Text.Json.Nodes;

namespace Panel.Cli;

/// <summary>
/// Что установщик читает из файлов панели, не разбирая их глубже одного ключа. На обеих
/// платформах пути разные, а файлы те же.
/// </summary>
internal static class PanelState
{
	/// <summary>Сколько серверов в реестре; <c>null</c>, если реестр не читается.</summary>
	public static int? RegisteredServers(string registry)
	{
		try
		{
			var node = JsonNode.Parse(File.ReadAllText(registry));
			return (node?["instances"] as JsonObject)?.Count ?? 0;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return null;
		}
	}

	/// <summary>Завершён ли мастер установки: <c>completed</c> в <c>setup.json</c>.</summary>
	public static bool SetupCompleted(string stateDirectory)
	{
		try
		{
			var node = JsonNode.Parse(File.ReadAllText(Path.Combine(stateDirectory, "setup.json")));
			return node?["completed"] is JsonValue flag && flag.TryGetValue<bool>(out var done) && done;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return false;
		}
	}

	/// <summary>Есть ли администратор: учётные записи или устаревший файл пароля.</summary>
	public static bool HasAdministrator(string stateDirectory) =>
		NonEmpty(Path.Combine(stateDirectory, "accounts.json")) || NonEmpty(Path.Combine(stateDirectory, "admin.password"));

	/// <summary>Адрес панели на этой машине: если панель слушает все адреса, 127.0.0.1.</summary>
	public static string LocalAddress(IReadOnlyDictionary<string, string> environment) =>
		Panel.Provisioning.Domain.PanelListener.FromEnvironment(environment).LocalUrl;

	/// <summary>Ссылка мастера из вывода <c>manage claim-token</c>; <c>null</c>, если не найдена.</summary>
	public static string? ClaimLink(string output) =>
		(output ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries)
			.FirstOrDefault(line => line.StartsWith("http", StringComparison.Ordinal) && line.Contains("/setup?token=", StringComparison.Ordinal));

	private static bool NonEmpty(string path)
	{
		var info = new FileInfo(path);
		return info.Exists && info.Length > 0;
	}
}
