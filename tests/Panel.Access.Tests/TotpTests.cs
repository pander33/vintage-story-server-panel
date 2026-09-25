using Panel.Access.Passwords;

namespace Panel.Access.Tests;

/// <summary>
/// RFC 6238 с секретом из RFC 4226 («12345678901234567890»), в base32
/// GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ.
/// </summary>
public sealed class TotpTests
{
	private const string secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

	[Theory]
	[InlineData(59, "287082")]
	[InlineData(1111111109, "081804")]
	[InlineData(1111111111, "050471")]
	[InlineData(1234567890, "005924")]
	[InlineData(2000000000, "279037")]
	public void Векторы_RFC_6238(long seconds, string expected) =>
		Assert.Equal(expected, Totp.Code(secret, DateTimeOffset.FromUnixTimeSeconds(seconds)));

	/// <summary>
	/// Окно в один шаг в обе стороны покрывает расхождение часов телефона и сервера.
	/// </summary>
	[Theory]
	[InlineData(-30, true)]
	[InlineData(0, true)]
	[InlineData(30, true)]
	[InlineData(-60, false)]
	[InlineData(60, false)]
	public void Окно_проверки_составляет_один_шаг(int offsetSeconds, bool accepted)
	{
		var moment = DateTimeOffset.FromUnixTimeSeconds(1111111111);
		var code = Totp.Code(secret, moment.AddSeconds(offsetSeconds));

		Assert.Equal(accepted, Totp.Verify(secret, code, moment));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("12345")]
	[InlineData("1234567")]
	[InlineData("abcdef")]
	public void Код_неверной_формы_отвергается(string? code) =>
		Assert.False(Totp.Verify(secret, code, DateTimeOffset.UnixEpoch));

	/// <summary>Короткий секрет означает незавершённую настройку.</summary>
	[Fact]
	public void Короткий_секрет_не_принимается_никогда()
	{
		const string Short = "GEZDGNBVGY3TQ";
		Assert.False(Totp.Verify(Short, Totp.Code(Short, DateTimeOffset.UnixEpoch), DateTimeOffset.UnixEpoch));
	}

	[Fact]
	public void Новый_секрет_годен_для_проверки()
	{
		var secret = Totp.NewSecret();
		var now = DateTimeOffset.UtcNow;

		Assert.True(secret.Length >= Totp.MinimumSecretLength);
		Assert.True(Totp.Verify(secret, Totp.Code(secret, now), now));
	}
}
