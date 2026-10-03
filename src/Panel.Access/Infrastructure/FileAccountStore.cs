using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Access.Domain;
using Panel.Access.Passwords;
using Panel.Access.Ports;
using Panel.SharedKernel;

namespace Panel.Access.Infrastructure;

/// <summary>
/// Учётные записи в <c>accounts.json</c> и однократный перенос пароля из устаревшего
/// <c>admin.password</c>.
/// </summary>
/// <remarks>
/// Запись переписывается поверх исходного JSON-объекта, поэтому
/// неизвестные этой версии поля сохраняются.
///
/// Отсутствие файла не ошибка: так выглядит панель до мастера установки. Нечитаемый или
/// испорченный файл — ошибка: если считать, что учётных записей нет, на работающей панели
/// откроется мастер установки.
/// </remarks>
public sealed class FileAccountStore : IAccountStore
{
	private readonly string path;
	private readonly string legacyPasswordPath;

	public FileAccountStore(string path, string legacyPasswordPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		this.path = path;
		this.legacyPasswordPath = legacyPasswordPath;
	}

	public async Task<Result<AccountDirectory>> LoadAsync(CancellationToken ct)
	{
		string json;
		try
		{
			json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
		{
			return Migrate();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Не читается {path}: {exception.Message}");
		}

		JsonNode? node;
		try
		{
			node = JsonNode.Parse(json);
		}
		catch (JsonException exception)
		{
			return Failure.Unavailable($"Файл учётных записей повреждён: {exception.Message}");
		}

		if (node is not JsonObject root || root["users"] is not JsonObject users)
		{
			return Failure.Unavailable("Файл учётных записей не содержит раздела users");
		}

		var accounts = new List<Account>();
		foreach (var (key, value) in users)
		{
			if (value is JsonObject entry && Map(key, entry) is { IsSuccess: true } account)
			{
				accounts.Add(account.Value);
			}

			// Испорченная запись пропускается, чтобы не терять остальные. Войти по ней нельзя.
		}

		var directory = new AccountDirectory(accounts);
		return directory.IsEmpty ? Migrate() : directory;
	}

	public async Task<Result<Done>> SaveAsync(AccountDirectory directory, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(directory);

		var users = new JsonObject();
		foreach (var account in directory.All)
		{
			users[account.Name.Value] = Serialize(account);
		}

		var root = new JsonObject { ["version"] = 1, ["users"] = users };
		var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
		return await AtomicFile.WriteAsync(path, json + "\n", ct).ConfigureAwait(false);
	}

	/// <summary>
	/// Устаревший формат: единственный пароль в отдельном файле. Перенос выполняется один раз,
	/// с поколением 0, как у уже выданных сессий.
	/// </summary>
	private Result<AccountDirectory> Migrate()
	{
		if (string.IsNullOrWhiteSpace(legacyPasswordPath))
		{
			return new AccountDirectory([]);
		}

		string encoded;
		try
		{
			encoded = File.ReadAllText(legacyPasswordPath).Trim();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return new AccountDirectory([]);
		}

		var password = PasswordHash.Parse(encoded);
		if (!password.IsSuccess)
		{
			return new AccountDirectory([]);
		}

		return new AccountDirectory([
			new Account
			{
				Name = UserName.Administrator,
				Password = password.Value,
				Role = Role.Admin,
				Generation = 0,
			},
		]);
	}

	private static Result<Account> Map(string key, JsonObject entry)
	{
		var name = UserName.Create(key);
		if (!name.IsSuccess)
		{
			return Result.Fail<Account>(name.Failure!);
		}

		var password = PasswordHash.Parse((string?) entry["password"]);
		if (!password.IsSuccess)
		{
			return Result.Fail<Account>(password.Failure!);
		}

		return new Account
		{
			Name = name.Value,
			Password = password.Value,
			Role = RoleWire.FromWire((string?) entry["role"]),
			Generation = (int?) entry["generation"] ?? 0,
			TotpSecret = (string?) entry["totp_secret"] ?? string.Empty,
			TotpEnabled = (bool?) entry["totp_enabled"] ?? false,
			PendingTotpSecret = (string?) entry["totp_pending_secret"] ?? string.Empty,
			Raw = entry,
		};
	}

	private static JsonObject Serialize(Account account)
	{
		// Копия исходной записи, если она была: неизвестные поля сохраняются.
		var entry = account.Raw?.DeepClone().AsObject() ?? [];

		entry["password"] = account.Password.Encode();
		entry["role"] = account.Role.ToWire();
		entry["generation"] = account.Generation;
		entry["totp_secret"] = account.TotpSecret;
		entry["totp_enabled"] = account.TotpEnabled;

		if (account.PendingTotpSecret.Length == 0)
		{
			entry.Remove("totp_pending_secret");
		}
		else
		{
			entry["totp_pending_secret"] = account.PendingTotpSecret;
		}

		return entry;
	}
}
