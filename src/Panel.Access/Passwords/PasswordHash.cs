using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Panel.SharedKernel;

namespace Panel.Access.Passwords;

/// <summary>
/// Хеш пароля: <c>scrypt$32768$8$1$&lt;salt&gt;$&lt;digest&gt;</c>, обе части в base64url.
/// </summary>
/// <remarks>
/// Формат заморожен (ADR-04, ADR-05). Параметры читаются из строки, а не из констант, иначе
/// хеш с другими параметрами перестал бы проверяться. Строка приходит с диска, поэтому
/// параметры проверяются здесь же.
///
/// Для перехода на Argon2id (ADR-05, вариант C) достаточно добавить разбор
/// <c>argon2id$…</c> и перехеширование при входе.
/// </remarks>
public sealed record PasswordHash
{
	/// <summary>Параметры для новых паролей.</summary>
	public const int DefaultN = 32768;
	public const int DefaultR = 8;
	public const int DefaultP = 1;
	public const int DefaultLength = 32;

	/// <summary>Минимальная длина пароля.</summary>
	public const int MinimumPasswordLength = 12;

	private PasswordHash(int n, int r, int p, byte[] salt, byte[] digest)
	{
		N = n;
		R = r;
		P = p;
		Salt = salt;
		Digest = digest;
	}

	public int N { get; }

	public int R { get; }

	public int P { get; }

	public byte[] Salt { get; }

	public byte[] Digest { get; }

	/// <summary>
	/// Нужно ли перехешировать пароль при следующем входе: параметры хеша слабее текущих умолчаний.
	/// </summary>
	public bool NeedsRehash => N < DefaultN || R < DefaultR || Digest.Length < DefaultLength;

	public static Result<PasswordHash> Parse(string? encoded)
	{
		var parts = (encoded ?? string.Empty).Trim().Split('$');
		if (parts.Length != 6 || parts[0] != "scrypt")
		{
			return Failure.Invalid("Неизвестный формат хеша пароля");
		}

		if (!int.TryParse(parts[1], CultureInfo.InvariantCulture, out var n)
			|| !int.TryParse(parts[2], CultureInfo.InvariantCulture, out var r)
			|| !int.TryParse(parts[3], CultureInfo.InvariantCulture, out var p))
		{
			return Failure.Invalid("Параметры хеша пароля не разобраны");
		}

		if (n < 2 || (n & (n - 1)) != 0 || r < 1 || p < 1 || 128L * r * n > Scrypt.MaxMemoryBytes)
		{
			return Failure.Invalid("Недопустимые параметры хеша пароля");
		}

		try
		{
			var salt = DecodeUrlSafe(parts[4]);
			var digest = DecodeUrlSafe(parts[5]);
			return salt.Length == 0 || digest.Length == 0
				? Failure.Invalid("Пустая соль или свёртка в хеше пароля")
				: new PasswordHash(n, r, p, salt, digest);
		}
		catch (FormatException)
		{
			return Failure.Invalid("Соль или свёртка в хеше пароля не являются base64url");
		}
	}

	public static Result<PasswordHash> Create(string password, byte[]? salt = null)
	{
		if ((password ?? string.Empty).Length < MinimumPasswordLength)
		{
			return Failure.Invalid($"Пароль должен содержать не менее {MinimumPasswordLength} символов");
		}

		salt ??= RandomNumberGenerator.GetBytes(16);
		var digest = Scrypt.DeriveKey(
			Encoding.UTF8.GetBytes(password!), salt, DefaultN, DefaultR, DefaultP, DefaultLength);

		return new PasswordHash(DefaultN, DefaultR, DefaultP, salt, digest);
	}

	/// <summary>
	/// Сравнение за постоянное время. Другая длина свёртки означает несовпадение, а не
	/// исключение: хеш мог создать другая версия.
	/// </summary>
	public bool Verify(string? password)
	{
		if (string.IsNullOrEmpty(password))
		{
			return false;
		}

		byte[] actual;
		try
		{
			actual = Scrypt.DeriveKey(Encoding.UTF8.GetBytes(password), Salt, N, R, P, Digest.Length);
		}
		catch (ArgumentOutOfRangeException)
		{
			return false;
		}

		var same = CryptographicOperations.FixedTimeEquals(actual, Digest);
		CryptographicOperations.ZeroMemory(actual);
		return same;
	}

	public string Encode() => string.Join('$',
		"scrypt",
		N.ToString(CultureInfo.InvariantCulture),
		R.ToString(CultureInfo.InvariantCulture),
		P.ToString(CultureInfo.InvariantCulture),
		EncodeUrlSafe(Salt),
		EncodeUrlSafe(Digest));

	/// <summary>
	/// base64url с выравниванием, в отличие от токена сессии: формат хеша требует знаков «=».
	/// </summary>
	private static string EncodeUrlSafe(byte[] value) =>
		Convert.ToBase64String(value).Replace('+', '-').Replace('/', '_');

	private static byte[] DecodeUrlSafe(string value)
	{
		var padded = value.Replace('-', '+').Replace('_', '/');
		padded += new string('=', (4 - (padded.Length % 4)) % 4);
		return Convert.FromBase64String(padded);
	}

	public override string ToString() => Encode();
}
