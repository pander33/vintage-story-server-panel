using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Panel.Access.Domain;
using Panel.Access.Sessions;
using Panel.SharedKernel;

namespace Panel.Access.Tests;

public sealed class SessionReaderTests
{
	private static readonly byte[] secret = Enumerable.Range(0, 48).Select(value => (byte) value).ToArray();

	private static readonly DateTimeOffset now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Слабый_ключ_отвергается()
	{
		var result = SessionSecret.Create(new byte[31]);

		Assert.False(result.IsSuccess);
		Assert.Equal(FailureCode.Invalid, result.Failure!.Code);
	}

	[Fact]
	public void Действующая_сессия_читается()
	{
		var reader = Reader();
		var token = Token("""{"exp":EXP,"csrf":"abc","user":"pavel","role":"operator","generation":3}""");

		var identity = reader.Read(token);

		Assert.True(identity.IsSuccess);
		Assert.Equal("pavel", identity.Value.User);
		Assert.Equal(Role.Operator, identity.Value.Role);
		Assert.Equal(3, identity.Value.Generation);
		Assert.Equal("abc", identity.Value.CsrfToken);
	}

	[Fact]
	public void Подделанная_подпись_отвергается()
	{
		var reader = Reader();
		var token = Token("""{"exp":EXP,"role":"viewer"}""");
		var forged = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');

		Assert.False(reader.Read(forged).IsSuccess);
	}

	[Fact]
	public void Изменённая_нагрузка_отвергается()
	{
		// Подпись считается от текста полезной нагрузки, поэтому подмена роли её ломает, даже если
		// JSON остаётся корректным.
		var token = Token("""{"exp":EXP,"role":"viewer"}""");
		var parts = token.Split('.');
		var swapped = Base64Url.EncodeToString(
			Encoding.UTF8.GetBytes($$"""{"exp":{{Expiry}},"role":"admin"}""")) + "." + parts[1];

		Assert.False(Reader().Read(swapped).IsSuccess);
	}

	[Fact]
	public void Истёкшая_сессия_отвергается()
	{
		var reader = Reader();
		var expired = Token("""{"exp":1,"role":"admin"}""", expiry: 1);

		Assert.False(reader.Read(expired).IsSuccess);
	}

	[Fact]
	public void Пустой_токен_отвергается()
	{
		Assert.False(Reader().Read(null).IsSuccess);
		Assert.False(Reader().Read(string.Empty).IsSuccess);
		Assert.False(Reader().Read("без-точки").IsSuccess);
	}

	[Fact]
	public void Неизвестная_роль_понижается_до_наблюдателя()
	{
		var identity = Reader().Read(Token("""{"exp":EXP,"role":"superuser"}"""));

		Assert.True(identity.IsSuccess);
		Assert.Equal(Role.Viewer, identity.Value.Role);
	}

	/// <summary>
	/// Токен без поля role означает администратора. Поведение опасное, но оставлено ради уже
	/// выданных токенов, и тест его фиксирует.
	/// </summary>
	[Fact]
	public void Токен_без_роли_читается_как_администратор_как_в_панели_1_2()
	{
		var identity = Reader().Read(Token("""{"exp":EXP}"""));

		Assert.True(identity.IsSuccess);
		Assert.Equal(Role.Admin, identity.Value.Role);
	}

	private static long Expiry => now.AddHours(1).ToUnixTimeSeconds();

	private static SessionReader Reader() =>
		new(SessionSecret.Create(secret).Value, new FixedTime(now));

	/// <summary>
	/// Токен как в make_session из security.py: base64url без выравнивания от JSON, точка,
	/// base64url от HMAC-SHA256 этой строки.
	/// </summary>
	private static string Token(string payloadTemplate, long? expiry = null)
	{
		var json = payloadTemplate.Replace("EXP", (expiry ?? Expiry).ToString(), StringComparison.Ordinal);
		var raw = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
		var signature = Base64Url.EncodeToString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(raw)));
		return raw + "." + signature;
	}

	private sealed class FixedTime : TimeProvider
	{
		private readonly DateTimeOffset now;

		public FixedTime(DateTimeOffset now) => this.now = now;

		public override DateTimeOffset GetUtcNow() => now;
	}
}
