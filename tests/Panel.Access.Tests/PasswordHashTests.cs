using Panel.Access.Passwords;

namespace Panel.Access.Tests;

public sealed class PasswordHashTests
{
	/// <summary>
	/// Формат строки заморожен. Знаки выравнивания base64 обязательны.
	/// </summary>
	[Fact]
	public void Форма_строки_совпадает_с_панелью_1_2()
	{
		var hash = PasswordHash.Create("достаточно длинный", new byte[16]).Value;
		var encoded = hash.Encode();
		var parts = encoded.Split('$');

		Assert.Equal(6, parts.Length);
		Assert.Equal("scrypt", parts[0]);
		Assert.Equal("32768", parts[1]);
		Assert.Equal("8", parts[2]);
		Assert.Equal("1", parts[3]);
		Assert.EndsWith("=", parts[4], StringComparison.Ordinal);
		Assert.EndsWith("=", parts[5], StringComparison.Ordinal);
	}

	[Fact]
	public void Разбор_и_запись_дают_ту_же_строку()
	{
		var original = PasswordHash.Create("достаточно длинный").Value.Encode();

		Assert.Equal(original, PasswordHash.Parse(original).Value.Encode());
	}

	[Fact]
	public void Верный_пароль_принимается_а_неверный_нет()
	{
		var hash = PasswordHash.Create("правильный пароль").Value;

		Assert.True(hash.Verify("правильный пароль"));
		Assert.False(hash.Verify("правильный пароль "));
		Assert.False(hash.Verify(string.Empty));
		Assert.False(hash.Verify(null));
	}

	[Fact]
	public void Короткий_пароль_отвергается_с_текстом_из_панели_1_2()
	{
		var result = PasswordHash.Create("коротко");

		Assert.False(result.IsSuccess);
		Assert.Equal("Пароль должен содержать не менее 12 символов", result.Failure!.Message);
	}

	[Theory]
	[InlineData("")]
	[InlineData("не-хеш")]
	[InlineData("argon2id$1$2$3$c2FsdA==$ZGlnZXN0")]
	[InlineData("scrypt$32768$8$1$c2FsdA==")]
	[InlineData("scrypt$x$8$1$c2FsdA==$ZGlnZXN0")]
	public void Негодная_строка_хеша_отвергается(string encoded) =>
		Assert.False(PasswordHash.Parse(encoded).IsSuccess);

	/// <summary>
	/// Параметры берутся из строки. С константами хеш, созданный с другими параметрами,
	/// перестал бы подходить.
	/// </summary>
	[Fact]
	public void Параметры_читаются_из_строки_а_не_из_констант()
	{
		var weak = PasswordHash.Parse(Encode(1024, 8, 1, "слабый но настоящий"));

		Assert.True(weak.IsSuccess);
		Assert.Equal(1024, weak.Value.N);
		Assert.True(weak.Value.Verify("слабый но настоящий"));
		Assert.True(weak.Value.NeedsRehash);
	}

	/// <summary>
	/// Строка хеша приходит с диска. Параметры, требующие больше памяти, чем разрешено,
	/// отвергаются при разборе, до вычисления.
	/// </summary>
	[Fact]
	public void Непомерные_параметры_отвергаются_при_разборе() =>
		Assert.False(PasswordHash.Parse("scrypt$1048576$8$1$c2FsdA==$ZGlnZXN0").IsSuccess);

	private static string Encode(int n, int r, int p, string password)
	{
		var salt = new byte[16];
		var digest = Scrypt.DeriveKey(System.Text.Encoding.UTF8.GetBytes(password), salt, n, r, p, 32);
		return string.Join('$', "scrypt", n, r, p, Base64(salt), Base64(digest));
	}

	private static string Base64(byte[] value) =>
		Convert.ToBase64String(value).Replace('+', '-').Replace('/', '_');
}
