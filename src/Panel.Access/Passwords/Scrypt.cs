using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Panel.Access.Passwords;

/// <summary>
/// scrypt по RFC 7914. В BCL его нет, реализация своя.
/// </summary>
/// <remarks>
/// Тесты проверяют векторы RFC 7914. Совпадение с настоящими хешами из accounts.json
/// проверяется только на сервере.
///
/// Реализация не оптимизирована: вызывается она только при входе, а параметры n=2^15, r=8
/// сами по себе дают около 100 мс и 32 МБ.
/// </remarks>
public static class Scrypt
{
	/// <summary>
	/// Предел памяти, как maxmem=64 МБ в security.py. Строка хеша приходит с диска, и без
	/// предела параметр n мог бы потребовать гигабайты.
	/// </summary>
	public const long MaxMemoryBytes = 64 * 1024 * 1024;

	public static byte[] DeriveKey(
		ReadOnlySpan<byte> password,
		ReadOnlySpan<byte> salt,
		int n,
		int r,
		int p,
		int length)
	{
		Validate(n, r, p, length);

		var blocks = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1, HashAlgorithmName.SHA256, p * 128 * r);

		var words = new uint[p * 32 * r];
		Buffer.BlockCopy(blocks, 0, words, 0, blocks.Length);
		EnsureLittleEndian(words);

		var scratch = new uint[n * 32 * r];
		for (var index = 0; index < p; index++)
		{
			Mix(words.AsSpan(index * 32 * r, 32 * r), scratch, n, r);
		}

		EnsureLittleEndian(words);
		Buffer.BlockCopy(words, 0, blocks, 0, blocks.Length);
		var key = Rfc2898DeriveBytes.Pbkdf2(password, blocks, 1, HashAlgorithmName.SHA256, length);

		CryptographicOperations.ZeroMemory(blocks);
		return key;
	}

	private static void Validate(int n, int r, int p, int length)
	{
		if (n < 2 || (n & (n - 1)) != 0)
		{
			throw new ArgumentOutOfRangeException(nameof(n), "Параметр n должен быть степенью двойки больше единицы");
		}

		ArgumentOutOfRangeException.ThrowIfLessThan(r, 1);
		ArgumentOutOfRangeException.ThrowIfLessThan(p, 1);
		ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);

		var required = 128L * r * n;
		if (required > MaxMemoryBytes)
		{
			throw new ArgumentOutOfRangeException(
				nameof(n), $"Параметры требуют {required / (1024 * 1024)} МБ при пределе {MaxMemoryBytes / (1024 * 1024)} МБ");
		}
	}

	/// <summary>scryptROMix из RFC 7914 §4.</summary>
	private static void Mix(Span<uint> block, uint[] scratch, int n, int r)
	{
		var size = 32 * r;
		Span<uint> x = stackalloc uint[16];
		var buffer = new uint[size];

		for (var index = 0; index < n; index++)
		{
			block.CopyTo(scratch.AsSpan(index * size, size));
			BlockMix(block, buffer, r, x);
			buffer.AsSpan(0, size).CopyTo(block);
		}

		for (var index = 0; index < n; index++)
		{
			// Integerify: младшее 32-битное слово последнего 64-байтового блока.
			// n — степень двойки, поэтому старшие биты на результат не влияют.
			var j = (int) (block[size - 16] & (uint) (n - 1));
			var source = scratch.AsSpan(j * size, size);
			for (var word = 0; word < size; word++)
			{
				block[word] ^= source[word];
			}

			BlockMix(block, buffer, r, x);
			buffer.AsSpan(0, size).CopyTo(block);
		}
	}

	/// <summary>scryptBlockMix из RFC 7914 §3.</summary>
	private static void BlockMix(ReadOnlySpan<uint> input, Span<uint> output, int r, Span<uint> x)
	{
		input.Slice((2 * r - 1) * 16, 16).CopyTo(x);

		for (var index = 0; index < 2 * r; index++)
		{
			var chunk = input.Slice(index * 16, 16);
			for (var word = 0; word < 16; word++)
			{
				x[word] ^= chunk[word];
			}

			Salsa8(x);

			var destination = (index % 2 == 0 ? index / 2 : r + index / 2) * 16;
			x.CopyTo(output.Slice(destination, 16));
		}
	}

	/// <summary>Salsa20/8 Core из RFC 7914 §3: восемь раундов, то есть четыре двойных.</summary>
	private static void Salsa8(Span<uint> block)
	{
		Span<uint> x = stackalloc uint[16];
		block.CopyTo(x);

		for (var round = 0; round < 4; round++)
		{
			// Столбцы.
			x[4] ^= Rotate(x[0] + x[12], 7);
			x[8] ^= Rotate(x[4] + x[0], 9);
			x[12] ^= Rotate(x[8] + x[4], 13);
			x[0] ^= Rotate(x[12] + x[8], 18);

			x[9] ^= Rotate(x[5] + x[1], 7);
			x[13] ^= Rotate(x[9] + x[5], 9);
			x[1] ^= Rotate(x[13] + x[9], 13);
			x[5] ^= Rotate(x[1] + x[13], 18);

			x[14] ^= Rotate(x[10] + x[6], 7);
			x[2] ^= Rotate(x[14] + x[10], 9);
			x[6] ^= Rotate(x[2] + x[14], 13);
			x[10] ^= Rotate(x[6] + x[2], 18);

			x[3] ^= Rotate(x[15] + x[11], 7);
			x[7] ^= Rotate(x[3] + x[15], 9);
			x[11] ^= Rotate(x[7] + x[3], 13);
			x[15] ^= Rotate(x[11] + x[7], 18);

			// Строки.
			x[1] ^= Rotate(x[0] + x[3], 7);
			x[2] ^= Rotate(x[1] + x[0], 9);
			x[3] ^= Rotate(x[2] + x[1], 13);
			x[0] ^= Rotate(x[3] + x[2], 18);

			x[6] ^= Rotate(x[5] + x[4], 7);
			x[7] ^= Rotate(x[6] + x[5], 9);
			x[4] ^= Rotate(x[7] + x[6], 13);
			x[5] ^= Rotate(x[4] + x[7], 18);

			x[11] ^= Rotate(x[10] + x[9], 7);
			x[8] ^= Rotate(x[11] + x[10], 9);
			x[9] ^= Rotate(x[8] + x[11], 13);
			x[10] ^= Rotate(x[9] + x[8], 18);

			x[12] ^= Rotate(x[15] + x[14], 7);
			x[13] ^= Rotate(x[12] + x[15], 9);
			x[14] ^= Rotate(x[13] + x[12], 13);
			x[15] ^= Rotate(x[14] + x[13], 18);
		}

		for (var word = 0; word < 16; word++)
		{
			block[word] += x[word];
		}
	}

	private static uint Rotate(uint value, int bits) => (value << bits) | (value >> (32 - bits));

	/// <summary>Перевод в прямой порядок байтов. На little-endian платформах ничего не делает.</summary>
	internal static void EnsureLittleEndian(Span<uint> words)
	{
		if (BitConverter.IsLittleEndian)
		{
			return;
		}

		for (var index = 0; index < words.Length; index++)
		{
			words[index] = BinaryPrimitives.ReverseEndianness(words[index]);
		}
	}
}
