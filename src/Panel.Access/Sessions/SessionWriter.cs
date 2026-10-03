using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Panel.Access.Domain;

namespace Panel.Access.Sessions;

/// <summary>Выданная сессия: токен для cookie и парный ему CSRF-токен.</summary>
public sealed record IssuedSession(string Token, string CsrfToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Выдача сессий: HMAC-SHA256 над base64url полезной нагрузки, обе части без выравнивания.
/// </summary>
/// <remarks>
/// Своя схема вместо Data Protection: поле <c>generation</c> отзывает сессии, а
/// встроенная cookie-аутентификация его не поддерживает.
/// </remarks>
public sealed class SessionWriter
{
	private readonly SessionSecret secret;
	private readonly TimeProvider time;

	public SessionWriter(SessionSecret secret, TimeProvider? time = null)
	{
		ArgumentNullException.ThrowIfNull(secret);
		this.secret = secret;
		this.time = time ?? TimeProvider.System;
	}

	public IssuedSession Issue(AuthenticatedUser user, TimeSpan lifetime)
	{
		ArgumentNullException.ThrowIfNull(user);

		var expiresAt = time.GetUtcNow() + lifetime;
		var csrf = RandomToken(24);

		var payload = new JsonObject
		{
			["exp"] = expiresAt.ToUnixTimeSeconds(),
			["csrf"] = csrf,

			// nonce никто не проверяет: он нужен, чтобы два входа в одну секунду давали разные токены.
			["nonce"] = RandomToken(12),
			["user"] = user.Name.Value,
			["role"] = user.Role.ToWire(),
			["generation"] = user.Generation,
		};

		var raw = System.Buffers.Text.Base64Url.EncodeToString(
			Encoding.UTF8.GetBytes(payload.ToJsonString()));
		var signature = System.Buffers.Text.Base64Url.EncodeToString(
			HMACSHA256.HashData(secret.Key, Encoding.UTF8.GetBytes(raw)));

		return new IssuedSession(raw + "." + signature, csrf, expiresAt);
	}

	private static string RandomToken(int bytes) =>
		System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));
}
