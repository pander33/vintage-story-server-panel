using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Panel.Access.Domain;
using Panel.SharedKernel;

namespace Panel.Access.Sessions;

/// <summary>
/// Разбор cookie <c>vs_session</c>. Формат заморожен (ADR-06): сессии, выданные до
/// обновления панели, продолжают действовать.
/// </summary>
/// <remarks>
/// Токен: <c>base64url(json).base64url(hmac_sha256(secret, base64url(json)))</c>, обе части
/// без <c>=</c>. Подпись считается от текста полезной нагрузки, поэтому перекодировать JSON
/// перед проверкой нельзя.
/// </remarks>
public sealed class SessionReader
{
	private readonly SessionSecret secret;
	private readonly TimeProvider time;

	public SessionReader(SessionSecret secret, TimeProvider? time = null)
	{
		ArgumentNullException.ThrowIfNull(secret);
		this.secret = secret;
		this.time = time ?? TimeProvider.System;
	}

	public Result<Identity> Read(string? token)
	{
		if (string.IsNullOrEmpty(token))
		{
			return Failure.Unauthenticated("Требуется вход");
		}

		var separator = token.IndexOf('.', StringComparison.Ordinal);
		if (separator <= 0 || separator == token.Length - 1)
		{
			return Failure.Unauthenticated("Требуется вход");
		}

		var raw = token[..separator];
		var signature = token[(separator + 1)..];

		var expected = Sign(raw);
		if (!CryptographicOperations.FixedTimeEquals(
				Encoding.UTF8.GetBytes(signature),
				Encoding.UTF8.GetBytes(expected)))
		{
			return Failure.Unauthenticated("Требуется вход");
		}

		if (!TryDecode(raw, out var payloadBytes))
		{
			return Failure.Unauthenticated("Требуется вход");
		}

		JsonNode? node;
		try
		{
			node = JsonNode.Parse(payloadBytes);
		}
		catch (JsonException)
		{
			return Failure.Unauthenticated("Требуется вход");
		}

		if (node is not JsonObject payload)
		{
			return Failure.Unauthenticated("Требуется вход");
		}

		if (payload["exp"] is not JsonValue expiry || !expiry.TryGetValue<long>(out var seconds))
		{
			return Failure.Unauthenticated("Требуется вход");
		}

		var expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
		if (expiresAt < time.GetUtcNow())
		{
			return Failure.Unauthenticated("Требуется вход");
		}

		// Без поля role сессия считается администраторской. Умолчание повышает права, но оставлено
		// ради совместимости уже выданных токенов; менять его нужно отдельным решением.
		var role = payload["role"] is JsonValue roleValue && roleValue.TryGetValue<string>(out var roleText)
			? RoleWire.FromWire(roleText)
			: Role.Admin;

		var user = payload["user"] is JsonValue userValue && userValue.TryGetValue<string>(out var userText)
			? userText
			: "admin";

		var generation = payload["generation"] is JsonValue generationValue
						 && generationValue.TryGetValue<int>(out var generationNumber)
			? generationNumber
			: 0;

		var csrf = payload["csrf"] is JsonValue csrfValue && csrfValue.TryGetValue<string>(out var csrfText)
			? csrfText
			: string.Empty;

		return new Identity(user, role, generation, csrf, expiresAt);
	}

	private string Sign(string raw)
	{
		var digest = HMACSHA256.HashData(secret.Key, Encoding.UTF8.GetBytes(raw));
		return Base64Url.EncodeToString(digest);
	}

	/// <summary>base64url без выравнивания.</summary>
	private static bool TryDecode(string value, out byte[] bytes)
	{
		try
		{
			bytes = Base64Url.DecodeFromChars(value);
			return true;
		}
		catch (FormatException)
		{
			bytes = [];
			return false;
		}
	}
}
