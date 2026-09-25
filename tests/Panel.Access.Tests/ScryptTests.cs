using System.Text;
using Panel.Access.Passwords;

namespace Panel.Access.Tests;

/// <summary>
/// Векторы RFC 7914 §12 (ADR-05).
/// </summary>
/// <remarks>
/// Настоящие хеши из accounts.json проверяет <see cref="RealPasswordTests"/>. Четвёртый вектор
/// RFC (n=1048576) не используется: ему нужен 1 ГБ, а панель ограничивает scrypt 64 МБ, как
/// maxmem в security.py.
/// </remarks>
public sealed class ScryptTests
{
	[Fact]
	public void Вектор_RFC_7914_пустой_пароль_и_соль()
	{
		var key = Scrypt.DeriveKey([], [], n: 16, r: 1, p: 1, length: 64);

		Assert.Equal(
			"77d6576238657b203b19ca42c18a0497f16b4844e3074ae8dfdffa3fede21442"
			+ "fcd0069ded0948f8326a753a0fc81f17e8d3e0fb2e0d3628cf35e20c38d18906",
			Convert.ToHexStringLower(key));
	}

	[Fact]
	public void Вектор_RFC_7914_password_NaCl()
	{
		var key = Scrypt.DeriveKey(
			Encoding.UTF8.GetBytes("password"),
			Encoding.UTF8.GetBytes("NaCl"),
			n: 1024, r: 8, p: 16, length: 64);

		Assert.Equal(
			"fdbabe1c9d3472007856e7190d01e9fe7c6ad7cbc8237830e77376634b373162"
			+ "2eaf30d92e22a3886ff109279d9830dac727afb94a83ee6d8360cbdfa2cc0640",
			Convert.ToHexStringLower(key));
	}

	[Fact]
	public void Вектор_RFC_7914_pleaseletmein_SodiumChloride()
	{
		var key = Scrypt.DeriveKey(
			Encoding.UTF8.GetBytes("pleaseletmein"),
			Encoding.UTF8.GetBytes("SodiumChloride"),
			n: 16384, r: 8, p: 1, length: 64);

		Assert.Equal(
			"7023bdcb3afd7348461c06cd81fd38ebfda8fbba904f8e3ea9b543f6545da1f2"
			+ "d5432955613f0fcf62d49705242a9af9e61e85dc0d651e40dfcf017b45575887",
			Convert.ToHexStringLower(key));
	}

	/// <summary>
	/// Параметры панели n=2^15, r=8, p=1 (32 МБ) проходят через ограничение памяти.
	/// </summary>
	[Fact]
	public void Параметры_панели_укладываются_в_предел_памяти()
	{
		var key = Scrypt.DeriveKey(
			Encoding.UTF8.GetBytes("достаточно длинный пароль"),
			[1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16],
			PasswordHash.DefaultN, PasswordHash.DefaultR, PasswordHash.DefaultP, PasswordHash.DefaultLength);

		Assert.Equal(32, key.Length);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(1)]
	[InlineData(3)]
	[InlineData(1 << 20)]
	public void Недопустимый_n_отвергается(int n) => Assert.Throws<ArgumentOutOfRangeException>(
		() => Scrypt.DeriveKey([1], [2], n, r: 8, p: 1, length: 32));
}
