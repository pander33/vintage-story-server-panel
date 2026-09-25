using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace Panel.Access.Passwords;

/// <summary>
/// Одноразовые коды по RFC 6238: HMAC-SHA1, шаг 30 секунд, шесть цифр.
/// </summary>
/// <remarks>
/// Окно проверки в три шага (предыдущий, текущий, следующий) покрывает расхождение часов
/// телефона и сервера.
///
/// SHA-1 нужен для совместимости с приложениями-аутентификаторами и уже заведёнными секретами.
/// </remarks>
public static class Totp
{
	public static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

	/// <summary>
	/// Минимальная длина секрета в символах base32, как в verify_totp: короткий секрет означает
	/// незавершённую настройку.
	/// </summary>
	public const int MinimumSecretLength = 16;

	public static string Code(string secret, DateTimeOffset at)
	{
		var counter = at.ToUnixTimeSeconds() / (long) Step.TotalSeconds;
		return CodeForCounter(Base32.Decode(secret), counter);
	}

	/// <summary>
	/// Проверка с окном в один шаг в обе стороны. Коды сравниваются за постоянное время.
	/// </summary>
	public static bool Verify(string? secret, string? code, DateTimeOffset at)
	{
		if (secret is null || secret.Length < MinimumSecretLength)
		{
			return false;
		}

		if (code is null || code.Length != 6 || !code.All(char.IsAsciiDigit))
		{
			return false;
		}

		byte[] key;
		try
		{
			key = Base32.Decode(secret);
		}
		catch (FormatException)
		{
			return false;
		}

		var counter = at.ToUnixTimeSeconds() / (long) Step.TotalSeconds;
		var matched = false;
		for (var offset = -1; offset <= 1; offset++)
		{
			// Без раннего выхода: цикл до конца независимо от совпадения.
			matched |= CryptographicOperations.FixedTimeEquals(
				System.Text.Encoding.ASCII.GetBytes(CodeForCounter(key, counter + offset)),
				System.Text.Encoding.ASCII.GetBytes(code));
		}

		return matched;
	}

	public static string NewSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(20));

	private static string CodeForCounter(byte[] key, long counter)
	{
		Span<byte> message = stackalloc byte[8];
		BinaryPrimitives.WriteInt64BigEndian(message, counter);

		Span<byte> digest = stackalloc byte[20];
		HMACSHA1.HashData(key, message, digest);

		var offset = digest[^1] & 0x0F;
		var truncated = BinaryPrimitives.ReadUInt32BigEndian(digest.Slice(offset, 4)) & 0x7FFFFFFF;
		return (truncated % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
	}
}

/// <summary>base32 по RFC 4648; в BCL его нет.</summary>
internal static class Base32
{
	private const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

	public static byte[] Decode(string value)
	{
		var text = value.TrimEnd('=').ToUpperInvariant();
		var bytes = new List<byte>(text.Length * 5 / 8);

		var buffer = 0;
		var bits = 0;
		foreach (var symbol in text)
		{
			var index = alphabet.IndexOf(symbol, StringComparison.Ordinal);
			if (index < 0)
			{
				throw new FormatException($"Недопустимый символ base32: {symbol}");
			}

			buffer = (buffer << 5) | index;
			bits += 5;
			if (bits >= 8)
			{
				bits -= 8;
				bytes.Add((byte) ((buffer >> bits) & 0xFF));
			}
		}

		return [.. bytes];
	}

	/// <summary>Без выравнивания.</summary>
	public static string Encode(ReadOnlySpan<byte> value)
	{
		var text = new System.Text.StringBuilder((value.Length * 8 + 4) / 5);
		var buffer = 0;
		var bits = 0;

		foreach (var octet in value)
		{
			buffer = (buffer << 8) | octet;
			bits += 8;
			while (bits >= 5)
			{
				bits -= 5;
				text.Append(alphabet[(buffer >> bits) & 0x1F]);
			}
		}

		if (bits > 0)
		{
			text.Append(alphabet[(buffer << (5 - bits)) & 0x1F]);
		}

		return text.ToString();
	}
}
