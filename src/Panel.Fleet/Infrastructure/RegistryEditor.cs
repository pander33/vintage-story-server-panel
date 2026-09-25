using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Fleet.Infrastructure;

/// <summary>
/// Изменение <c>/etc/vs-panel/instances.json</c>: чтение под блокировкой, изменение, атомарная
/// запись. Используется только привилегированной стороной.
/// </summary>
/// <remarks>
/// <para>
/// Блокировка — файл <c>instances.json.lock</c>: реестр меняют несколько процессов, и без неё
/// одновременные «переименовать» и «забыть» потеряли бы одно из изменений. Реестр
/// перечитывается уже под блокировкой.
/// </para>
/// <para>
/// Записи хранятся целиком, неизвестные этой версии ключи сохраняются. Реестр версии 1
/// (плоский <c>vs-panel.json</c>) только для чтения.
/// </para>
/// </remarks>
public sealed class RegistryEditor
{
	private static readonly TimeSpan lockWait = TimeSpan.FromSeconds(30);

	private readonly string panelConfigPath;

	public RegistryEditor(string panelConfigPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(panelConfigPath);
		this.panelConfigPath = panelConfigPath;
	}

	/// <summary>Можно ли менять реестр: версия 2 настроек панели.</summary>
	public async Task<bool> IsWritableAsync(CancellationToken ct) =>
		await ConfigurationAsync(ct).ConfigureAwait(false) is { IsSuccess: true } configuration
		&& !configuration.Value.IsReadOnlyRegistry;

	/// <summary>
	/// Изменить реестр одной операцией. <paramref name="change"/> получает документ под
	/// блокировкой и возвращает результат или отказ; при отказе файл не меняется.
	/// </summary>
	public async Task<Result<T>> EditAsync<T>(Func<RegistryDocument, Result<T>> change, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(change);

		var configuration = await ConfigurationAsync(ct).ConfigureAwait(false);
		if (!configuration.IsSuccess)
		{
			return Result.Fail<T>(configuration.Failure!);
		}

		if (configuration.Value.IsReadOnlyRegistry)
		{
			return Failure.Conflict(
				"Реестр доступен только для чтения, пока /etc/vs-panel.json версии 1; выполните scripts/install.sh");
		}

		var path = configuration.Value.InstancesFile;
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);

		using var guard = await AcquireAsync(path + ".lock", ct).ConfigureAwait(false);
		if (guard is null)
		{
			return Failure.Unavailable("Реестр занят другой операцией; повторите позже");
		}

		var document = await ReadAsync(path, ct).ConfigureAwait(false);
		if (!document.IsSuccess)
		{
			return Result.Fail<T>(document.Failure!);
		}

		var outcome = change(document.Value);
		if (!outcome.IsSuccess)
		{
			return outcome;
		}

		var written = await WriteAsync(path, document.Value, ct).ConfigureAwait(false);
		return written.IsSuccess ? outcome : Result.Fail<T>(written.Failure!);
	}

	private async Task<Result<PanelConfiguration>> ConfigurationAsync(CancellationToken ct)
	{
		try
		{
			return PanelConfiguration.Parse(await File.ReadAllTextAsync(panelConfigPath, ct).ConfigureAwait(false));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Не читается {panelConfigPath}: {exception.Message}");
		}
	}

	private static async Task<Result<RegistryDocument>> ReadAsync(string path, CancellationToken ct)
	{
		if (!File.Exists(path))
		{
			return new RegistryDocument(new JsonObject(), null);
		}

		try
		{
			var root = JsonNode.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)) as JsonObject;
			if (root?["instances"] is not JsonObject instances)
			{
				return Failure.Unavailable("Реестр не содержит раздела instances");
			}

			var declared = InstanceId.Create((string?) root["default"]);
			return new RegistryDocument((JsonObject) instances.DeepClone(), declared.IsSuccess ? declared.Value : null);
		}
		catch (JsonException exception)
		{
			return Failure.Unavailable($"Реестр повреждён: {exception.Message}");
		}
	}

	/// <summary>
	/// <c>{"version": 1, "default", "instances"}</c>, отступ в два пробела, права 0644, потому что
	/// панель читает реестр без прав root.
	/// </summary>
	private static async Task<Result<Done>> WriteAsync(string path, RegistryDocument document, CancellationToken ct)
	{
		var payload = new JsonObject
		{
			["version"] = 1,
			["default"] = document.Default?.Value,
			["instances"] = document.Instances.DeepClone(),
		};

		var options = new JsonSerializerOptions
		{
			WriteIndented = true,
			Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		};

		var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Environment.ProcessId}.tmp");
		try
		{
			await File.WriteAllTextAsync(temporary, payload.ToJsonString(options) + "\n", new UTF8Encoding(false), ct)
				.ConfigureAwait(false);

			if (!OperatingSystem.IsWindows())
			{
				File.SetUnixFileMode(
					temporary,
					UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
			}

			File.Move(temporary, path, overwrite: true);
			return Done.Ok;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Не удалось записать реестр: {exception.Message}");
		}
		finally
		{
			if (File.Exists(temporary))
			{
				File.Delete(temporary);
			}
		}
	}

	/// <summary>
	/// Исключительная блокировка файла. На Linux <see cref="FileShare.None"/> даёт
	/// <c>flock(LOCK_EX | LOCK_NB)</c>. Ожидание сделано повторными попытками, потому что
	/// неблокирующий вызов ждать не умеет.
	/// </summary>
	private static async Task<FileStream?> AcquireAsync(string lockPath, CancellationToken ct)
	{
		var deadline = DateTime.UtcNow + lockWait;
		while (true)
		{
			try
			{
				return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
			}
			catch (IOException) when (DateTime.UtcNow < deadline)
			{
				await Task.Delay(100, ct).ConfigureAwait(false);
			}
			catch (IOException)
			{
				return null;
			}
		}
	}
}

/// <summary>Изменяемый реестр: записи серверов как есть и умолчание.</summary>
public sealed class RegistryDocument
{
	public RegistryDocument(JsonObject instances, InstanceId? defaultInstance)
	{
		Instances = instances;
		Default = defaultInstance;
	}

	public JsonObject Instances { get; }

	public InstanceId? Default { get; set; }

	public IReadOnlyCollection<InstanceId> Registered =>
	[
		.. Instances
			.Select(pair => InstanceId.Create(pair.Key))
			.Where(id => id.IsSuccess)
			.Select(id => id.Value),
	];
}
