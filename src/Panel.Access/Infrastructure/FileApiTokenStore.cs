using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Access.Domain;
using Panel.Access.Ports;
using Panel.SharedKernel;

namespace Panel.Access.Infrastructure;

/// <summary>
/// API-токены в <c>api-tokens.json</c> каталога состояния панели, права 0600.
/// </summary>
/// <remarks>
/// <c>{"version": 1, "tokens": [{"id", "name", "user", "role", "generation", "hash",
/// "created_at", "expires_at"}]}</c>. Секретов в файле нет, только SHA-256, но список
/// токенов и их владельцев — тоже сведения о доступе, поэтому файл закрыт, как
/// <c>accounts.json</c>.
///
/// Отсутствие файла — пустой набор: так выглядит панель, где токенов ещё не выпускали.
/// Испорченный файл — отказ: молча пустой набор отозвал бы все токены до следующей записи,
/// а запись затёрла бы файл, который ещё можно починить. Испорченная запись пропускается.
///
/// Прочитанный набор помнится, пока у файла те же время правки и размер: токен проверяется на
/// каждый запрос, и неверный токен не должен стоить чтения и разбора файла. Своя запись сбрасывает
/// память сразу.
/// </remarks>
public sealed class FileApiTokenStore : IApiTokenStore
{
	private readonly string path;
	private readonly SemaphoreSlim changing = new(1, 1);
	private volatile Cached? cached;

	public FileApiTokenStore(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		this.path = path;
	}

	public async Task<Result<ApiTokenSet>> LoadAsync(CancellationToken ct)
	{
		// Отметка берётся до чтения: если файл изменится между ними, следующая отметка не совпадёт,
		// и файл перечитается.
		var file = new FileInfo(path);
		var stamp = file.Exists ? new Stamp(file.LastWriteTimeUtc, file.Length) : (Stamp?) null;
		if (stamp is not null && cached is { } hit && hit.Stamp == stamp)
		{
			return hit.Tokens;
		}

		string json;
		try
		{
			json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
		{
			return ApiTokenSet.Empty;
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
			return Failure.Unavailable($"Файл API-токенов повреждён: {exception.Message}");
		}

		if (node is not JsonObject root || root["tokens"] is not JsonArray items)
		{
			return Failure.Unavailable("Файл API-токенов не содержит раздела tokens");
		}

		var tokens = new ApiTokenSet(items.OfType<JsonObject>().Select(Map).OfType<ApiToken>());
		if (stamp is { } seen)
		{
			cached = new Cached(seen, tokens);
		}

		return tokens;
	}

	public async Task<Result<T>> ChangeAsync<T>(
		Func<ApiTokenSet, Result<(ApiTokenSet Tokens, T Value)>> change, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(change);

		await changing.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			var current = await LoadAsync(ct).ConfigureAwait(false);
			if (!current.IsSuccess)
			{
				return current.Failure!;
			}

			var changed = change(current.Value);
			if (!changed.IsSuccess)
			{
				return changed.Failure!;
			}

			cached = null;
			var written = await WriteAsync(changed.Value.Tokens, ct).ConfigureAwait(false);
			return written.IsSuccess ? changed.Value.Value : written.Failure!;
		}
		finally
		{
			changing.Release();
		}
	}

	private async Task<Result<Done>> WriteAsync(ApiTokenSet tokens, CancellationToken ct)
	{
		var root = new JsonObject
		{
			["version"] = 1,
			["tokens"] = new JsonArray([.. tokens.All.Select(token => (JsonNode?) Serialize(token))]),
		};

		var written = await AtomicFile
			.WriteAsync(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", ct)
			.ConfigureAwait(false);
		if (written.IsSuccess && !OperatingSystem.IsWindows())
		{
			// Новый файл получает 0600, а не права по umask; на Windows доступ задаёт список доступа
			// каталога состояния.
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}

		return written;
	}

	private static ApiToken? Map(JsonObject entry)
	{
		var id = ApiTokenId.Parse((string?) Value(entry["id"]));
		var name = ApiTokenName.Create((string?) Value(entry["name"]));
		var user = UserName.Create((string?) Value(entry["user"]) ?? string.Empty);
		var hash = (string?) Value(entry["hash"]);
		var created = Timestamp(entry["created_at"]);
		var expires = Timestamp(entry["expires_at"]);

		if (!id.IsSuccess || !name.IsSuccess || !user.IsSuccess || hash is not { Length: 64 } || created is null
			|| (entry["expires_at"] is not null && expires is null))
		{
			return null;
		}

		return new ApiToken
		{
			Id = id.Value,
			Name = name.Value,
			User = user.Value,
			Role = RoleWire.FromWire((string?) Value(entry["role"])),
			Generation = entry["generation"] is JsonValue generation && generation.TryGetValue<int>(out var number) ? number : 0,
			Hash = hash,
			CreatedAt = created.Value,
			ExpiresAt = expires,
		};
	}

	/// <summary>Секунды Unix допустимого диапазона; неверная дата исключает только свою запись.</summary>
	private static DateTimeOffset? Timestamp(JsonNode? node) =>
		node is JsonValue value && value.TryGetValue<long>(out var seconds)
			&& seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds()
			&& seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
			? DateTimeOffset.FromUnixTimeSeconds(seconds)
			: null;

	private readonly record struct Stamp(DateTime Modified, long Length);

	private sealed record Cached(Stamp Stamp, ApiTokenSet Tokens);

	/// <summary>Строковое значение или <c>null</c>, если поле другого типа.</summary>
	private static JsonValue? Value(JsonNode? node) =>
		node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value : null;

	private static JsonObject Serialize(ApiToken token) => new()
	{
		["id"] = token.Id.Value,
		["name"] = token.Name.Value,
		["user"] = token.User.Value,
		["role"] = token.Role.ToWire(),
		["generation"] = token.Generation,
		["hash"] = token.Hash,
		["created_at"] = token.CreatedAt.ToUnixTimeSeconds(),
		["expires_at"] = token.ExpiresAt?.ToUnixTimeSeconds(),
	};
}
