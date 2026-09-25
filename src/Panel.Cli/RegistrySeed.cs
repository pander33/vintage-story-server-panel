using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.SharedKernel;

namespace Panel.Cli;

/// <summary>Что установщик делает с реестром серверов.</summary>
internal abstract record RegistryDecision
{
	/// <summary>Реестр уже есть, его не трогают: в нём может быть несколько серверов.</summary>
	public sealed record Keep : RegistryDecision;

	/// <summary>Плоский <c>/etc/vs-panel.json</c> версии 1 переносится в реестр сервером <c>main</c>.</summary>
	public sealed record MigrateFlat(string Json) : RegistryDecision;

	/// <summary>Сервер в <c>/srv/vintagestory</c> поставлен вручную до панели и берётся под управление.</summary>
	public sealed record AdoptLegacy(string Json) : RegistryDecision;

	/// <summary>Чистая машина: пустой реестр, первый сервер создаст мастер или установщик.</summary>
	public sealed record CreateEmpty(string Json) : RegistryDecision;
}

/// <summary>
/// Реестр пишется один раз, при первой установке.
/// </summary>
/// <remarks>
/// Решение без побочных эффектов: на входе прочитанное с диска, на выходе то, что нужно
/// записать. Формат файла заморожен (ADR-11): <c>json.dumps(ensure_ascii=False, indent=2)</c>
/// и перевод строки в конце.
/// </remarks>
internal static class RegistrySeed
{
	/// <summary>Ключи панели в плоском файле версии 1; в описание сервера они не переносятся.</summary>
	public static IReadOnlySet<string> PanelKeys { get; } =
		new HashSet<string>(["staging_dir", "panel_env_file", "version", "layout", "instances_file"], StringComparer.Ordinal);

	public const string EmptyRegistry = "{\n  \"version\": 1,\n  \"default\": \"\",\n  \"instances\": {}\n}\n";

	private static readonly JsonSerializerOptions written = new()
	{
		WriteIndented = true,
		IndentSize = 2,
		NewLine = "\n",
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	/// <param name="registryExists">Есть ли <c>/etc/vs-panel/instances.json</c>.</param>
	/// <param name="panelConfig">Текст <c>/etc/vs-panel.json</c>; <c>null</c>, если файла нет.</param>
	/// <param name="legacyServer">Есть ли непустой <c>/srv/vintagestory/data/serverconfig.json</c>.</param>
	/// <param name="legacyTemplate">Шаблон реестра для сервера в <c>/srv/vintagestory</c>.</param>
	public static Result<RegistryDecision> Decide(bool registryExists, string? panelConfig, bool legacyServer, string legacyTemplate)
	{
		if (registryExists)
		{
			return new RegistryDecision.Keep();
		}

		// Непустой /etc/vs-panel.json без ключа "version" — плоский файл версии 1.
		if (!string.IsNullOrEmpty(panelConfig))
		{
			JsonNode? node;
			try
			{
				node = JsonNode.Parse(panelConfig);
			}
			catch (JsonException exception)
			{
				return Failure.Invalid($"/etc/vs-panel.json не разбирается, реестр серверов не создан: {exception.Message}");
			}

			if (node is not JsonObject legacy)
			{
				return Failure.Invalid("/etc/vs-panel.json не является объектом JSON, реестр серверов не создан");
			}

			if (!legacy.ContainsKey("version"))
			{
				return new RegistryDecision.MigrateFlat(Migrate(legacy));
			}
		}

		return legacyServer
			? new RegistryDecision.AdoptLegacy(legacyTemplate.Replace("\r\n", "\n", StringComparison.Ordinal))
			: new RegistryDecision.CreateEmpty(EmptyRegistry);
	}

	/// <summary>Описание сервера: ключи файла без панельных, имя по умолчанию «Основной сервер».</summary>
	private static string Migrate(JsonObject legacy)
	{
		var instance = new JsonObject();
		foreach (var (key, value) in legacy)
		{
			if (!PanelKeys.Contains(key))
			{
				instance[key] = value?.DeepClone();
			}
		}

		if (!instance.ContainsKey("name"))
		{
			instance["name"] = "Основной сервер";
		}

		var registry = new JsonObject
		{
			["version"] = 1,
			["default"] = "main",
			["instances"] = new JsonObject { ["main"] = instance },
		};
		return registry.ToJsonString(written) + "\n";
	}
}
