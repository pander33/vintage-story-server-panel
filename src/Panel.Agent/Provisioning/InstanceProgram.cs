using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Agent.Content;
using Panel.Content.Domain;
using Panel.Fleet.Domain;
using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Provisioning;

/// <summary>Подготовка программы и настроек нового экземпляра; общие правила создания и импорта.</summary>
internal static class InstanceProgram
{
	private static readonly UnixFileMode mode700 =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
	private static readonly UnixFileMode mode644 =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

	private static readonly TimeSpan genconfigTimeout = TimeSpan.FromSeconds(300);

	private static readonly UnixFileMode mode640 =
		UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

	/// <summary>Распаковать пакет рядом и переместить каталог программы на место.</summary>
	internal static async Task<Result<Done>> ExtractProgramAsync(
		string package,
		PackageLayout details,
		InstanceLayout paths,
		CancellationToken ct)
	{
		var extractRoot = Path.Combine(
			paths.Root,
			".vs-instance-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant());
		try
		{
			Directory.CreateDirectory(extractRoot);
			GameFiles.SetMode(extractRoot, mode700);
			var extracted = await PackageArchive.ExtractAsync(package, extractRoot, ct).ConfigureAwait(false);
			if (!extracted.IsSuccess)
			{
				return extracted.Failure!;
			}

			var candidate = details.Root.Length > 0 ? Path.Combine(extractRoot, details.Root) : extractRoot;
			if (!File.Exists(Path.Combine(candidate, ServerPackage.ServerDll)))
			{
				return Failure.Invalid("Extracted package has no server DLL");
			}

			Directory.Move(candidate, paths.ProgramDirectory);
			return Done.Ok;
		}
		finally
		{
			ProvisioningFiles.TryDelete(extractRoot);
		}
	}

	/// <summary>
	/// Игра пишет полную конфигурацию сама, панель записывает поверх свои значения.
	/// <c>--genconfig</c> работает только на пустом dataPath, а здесь он пустой.
	/// </summary>
	internal static async Task<Result<Done>> GenerateConfigAsync(
		IProvisioningPlatform platform,
		string dotnet,
		ServerSpec spec,
		InstanceLayout paths,
		CancellationToken ct)
	{
		var program = Path.Combine(paths.ProgramDirectory, ServerPackage.ServerDll);
		if (!File.Exists(program))
		{
			return Failure.Unavailable("Server program directory has no VintagestoryServer.dll");
		}

		HostCommandResult result;
		try
		{
			result = await platform.GenerateConfigAsync(dotnet, paths, genconfigTimeout, ct).ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
			return Failure.Unavailable("The game did not finish --genconfig within 300 seconds");
		}

		if (!File.Exists(paths.ConfigFile))
		{
			return Failure.Unavailable(result.Tail("The game did not create serverconfig.json"));
		}

		JsonObject generated;
		try
		{
			// ReadAllText сам снимает BOM.
			if (JsonNode.Parse(await File.ReadAllTextAsync(paths.ConfigFile, ct).ConfigureAwait(false)) is not JsonObject value)
			{
				return Failure.Unavailable("The generated serverconfig.json is not a JSON object");
			}

			generated = value;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
		{
			return Failure.Unavailable($"Cannot read the generated serverconfig.json: {exception.Message}");
		}

		var applied = InitialConfig.Apply(generated, spec, paths);
		var options = new JsonSerializerOptions
		{
			WriteIndented = true,
			Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		};
		await File.WriteAllTextAsync(paths.ConfigFile, applied.ToJsonString(options) + "\n", new UTF8Encoding(false), ct)
			.ConfigureAwait(false);
		GameFiles.SetMode(paths.ConfigFile, mode640);
		return Done.Ok;
	}

	/// <summary>
	/// Копирует самый новый архив мода телеметрии, оставленный установщиком, чтобы можно было
	/// проверить готовность нового сервера. Если архивов нет, возвращает пустую строку.
	/// </summary>
	internal static string SeedTelemetryMod(string telemetryDirectory, string modsDirectory)
	{
		if (telemetryDirectory.Length == 0 || !Directory.Exists(telemetryDirectory))
		{
			return string.Empty;
		}

		var newest = Directory.EnumerateFiles(telemetryDirectory, "vspaneltelemetry_*.zip")
			.Select(path => new FileInfo(path))
			.Where(info => info is { Exists: true, LinkTarget: null })
			.OrderBy(info => info.Name, StringComparer.Ordinal)
			.LastOrDefault();
		if (newest is null)
		{
			return string.Empty;
		}

		var target = Path.Combine(modsDirectory, newest.Name);
		File.Copy(newest.FullName, target, overwrite: true);
		GameFiles.SetMode(target, mode644);
		return newest.Name;
	}

	/// <summary>Идентификатор или порт уже заняты.</summary>
	internal static Failure? Clash(IEnumerable<(string Id, int Port)> registered, ServerSpec spec)
	{
		foreach (var (id, port) in registered)
		{
			if (id == spec.Id.Value)
			{
				return Failure.Conflict($"Instance {spec.Id.Value} already exists");
			}

			if (port == spec.Port)
			{
				return Failure.Conflict($"Port {spec.Port.ToString(CultureInfo.InvariantCulture)} is already used by instance {id}");
			}
		}

		return null;
	}

	internal static int PortOf(JsonObject? entry) =>
		entry?["port"] is JsonValue value && value.TryGetValue<int>(out var port) ? port : 0;

	internal static Failure Abandon(InstanceLayout paths, Failure reason)
	{
		ProvisioningFiles.TryDelete(paths.Root);
		return reason;
	}
}
