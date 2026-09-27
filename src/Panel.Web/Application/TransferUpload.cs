using System.Security.Cryptography;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>Потоковая запись переносимого пакета с независимым пределом и SHA256.</summary>
public static class TransferUpload
{
	public static async Task<Result<(string Sha256, long Length)>> WriteAsync(Stream source, string path, long limit, CancellationToken ct)
	{
		if (limit <= 0)
		{
			return Failure.Invalid("Недопустимый предел размера пакета");
		}

		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
		{
			using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
			var buffer = new byte[1024 * 1024];
			long length = 0;
			int read;
			while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
			{
				if (read > limit - length)
				{
					return Failure.TooLarge("Переносимый пакет превышает допустимый размер");
				}

				await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
				hash.AppendData(buffer, 0, read);
				length += read;
			}

			if (length == 0)
			{
				return Failure.Invalid("Пустой переносимый пакет");
			}

			if (OperatingSystem.IsLinux())
			{
				File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			}

			return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), length);
		}
	}
}
