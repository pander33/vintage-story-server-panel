using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Panel.Access.Domain;

namespace Panel.Access.Tests;

/// <summary>
/// Сборка токена сессии для тестов.
/// </summary>
/// <remarks>
/// Это проверка алгоритма по его описанию. На настоящем токене из fixtures/session-token.txt
/// формат проверяет <see cref="RealTokenTests"/>.
/// </remarks>
internal static class SessionFixture
{
	public static byte[] Secret { get; } = [.. Enumerable.Range(0, 48).Select(value => (byte) (value + 7))];

	public static string Token(Role role)
	{
		var expiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
		var json = $$"""{"exp":{{expiry}},"csrf":"t","nonce":"n","user":"tester","role":"{{role.ToWire()}}","generation":0}""";
		var raw = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
		var signature = Base64Url.EncodeToString(HMACSHA256.HashData(Secret, Encoding.UTF8.GetBytes(raw)));
		return raw + "." + signature;
	}
}
