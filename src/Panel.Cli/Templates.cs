using System.Globalization;

namespace Panel.Cli;

/// <summary>
/// Файлы, которые ставит установщик: шаблоны из <c>deploy/</c>, встроенные в сборку.
/// </summary>
/// <remarks>
/// Всё, что отсюда записывается на диск, разбирает Linux: systemd, sh, <c>EnvironmentFile=</c>.
/// Переводы строк приводятся к LF здесь, а не только в <c>.gitattributes</c>: сборка может идти
/// на Windows, а git защищает репозиторий, но не рабочую копию. CR systemd считает частью
/// значения, а sh — частью команды.
/// </remarks>
internal static class Templates
{
	public const string UidPlaceholder = "@PANEL_UID@";

	public static string PanelUnit => Read("vs-panel.service");

	public static string PanelConfig => Read("vs-panel.json");

	public static string PanelEnvironment => Read("vs-panel.env");

	public static string LegacyRegistry => Read("vs-panel-instances.json");

	public static string ConsoleWrapper => Read("vs-console-wrapper");

	/// <summary>Юнит демона: сокет открыт только одному uid, служебной учётной записи панели.</summary>
	public static string AgentUnit(int panelUid)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(panelUid);
		return Read("vs-panel-agent.service").Replace(UidPlaceholder, panelUid.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
	}

	public const string DataRootPlaceholder = "@DATA_ROOT@";

	/// <summary>
	/// <c>vs-panel.json</c> раскладки Windows с настоящим каталогом данных: <c>ProgramData</c>
	/// может быть не на диске C:. Путь вставляется в строки JSON и поэтому экранируется.
	/// </summary>
	public static string WindowsPanelConfig(string dataRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
		var escaped = System.Text.Json.JsonSerializer.Serialize(dataRoot, relaxedJson)[1..^1];
		return Read("windows.vs-panel.json").Replace(DataRootPlaceholder, escaped, StringComparison.Ordinal);
	}

	/// <summary><c>vs-panel.env</c> для Windows; состояние хранится в каталоге данных.</summary>
	public static string WindowsPanelEnvironment(string dataRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
		return Read("windows.vs-panel.env").Replace(DataRootPlaceholder, dataRoot, StringComparison.Ordinal);
	}

	private static readonly System.Text.Json.JsonSerializerOptions relaxedJson = new()
	{
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	private static readonly System.Text.Json.JsonSerializerOptions indentedJson = new()
	{
		Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		WriteIndented = true,
	};

	/// <summary>Ключи <c>vs-panel.json</c>, которые выбирает оператор, а не шаблон.</summary>
	private static readonly string[] chosenKeys = ["instances_root", "former_instances_roots"];

	/// <summary>
	/// Шаблон <c>vs-panel.json</c> с каталогом серверов из прежнего файла, чтобы обновление не
	/// сбросило к умолчанию каталог, выбранный при установке или в настройках панели.
	/// </summary>
	/// <param name="template">Новый файл из шаблона.</param>
	/// <param name="existing">Прежний файл; <see langword="null"/>, если его не было.</param>
	public static string KeepChosenPaths(string template, string? existing)
	{
		ArgumentNullException.ThrowIfNull(template);
		System.Text.Json.Nodes.JsonObject? previous;
		try
		{
			previous = existing is null ? null : System.Text.Json.Nodes.JsonNode.Parse(existing) as System.Text.Json.Nodes.JsonObject;
		}
		catch (System.Text.Json.JsonException)
		{
			previous = null;
		}

		if (previous is null || System.Text.Json.Nodes.JsonNode.Parse(template) is not System.Text.Json.Nodes.JsonObject result)
		{
			return template;
		}

		var changed = false;
		foreach (var key in chosenKeys)
		{
			if (previous[key] is { } value
				&& (value is System.Text.Json.Nodes.JsonArray || (value is System.Text.Json.Nodes.JsonValue text && text.TryGetValue<string>(out var path) && path.Length > 0))
				&& !System.Text.Json.Nodes.JsonNode.DeepEquals(result[key], value))
			{
				result[key] = value.DeepClone();
				changed = true;
			}
		}

		return changed ? result.ToJsonString(indentedJson) + "\n" : template;
	}

	public static string Read(string name)
	{
		using var stream = typeof(Templates).Assembly.GetManifestResourceStream("Panel.Cli.Deploy." + name)
			?? throw new InvalidOperationException($"Template {name} is not embedded in the assembly");
		using var reader = new StreamReader(stream);
		return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
	}
}

/// <summary>Построчный <c>KEY=VALUE</c>: формат <c>vs-panel.env</c>, который пишут установщик и демон.</summary>
internal static class EnvironmentFile
{
	public static Dictionary<string, string> Parse(string? text)
	{
		var values = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var raw in (text ?? string.Empty).Split('\n'))
		{
			var line = raw.Trim();
			var separator = line.IndexOf('=', StringComparison.Ordinal);
			if (line.Length == 0 || line.StartsWith('#') || separator <= 0)
			{
				continue;
			}

			values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
		}

		return values;
	}

	public static Dictionary<string, string> Load(string path)
	{
		try
		{
			return Parse(File.ReadAllText(path));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return new Dictionary<string, string>(StringComparer.Ordinal);
		}
	}

	public static string Value(IReadOnlyDictionary<string, string> values, string key, string fallback) =>
		values.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;
}
