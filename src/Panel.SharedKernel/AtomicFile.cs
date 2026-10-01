using System.Text;

namespace Panel.SharedKernel;

/// <summary>
/// Запись файла состояния целиком за один шаг.
/// </summary>
/// <remarks>
/// Читатель не должен увидеть половину файла: панель может перезапуститься посреди записи, а
/// с половиной <c>accounts.json</c> в неё никто не войдёт.
///
/// <see cref="File.Move(string, string, bool)"/> на Linux — это <c>rename(2)</c>, атомарный в
/// пределах файловой системы; на Windows он атомарен в пределах тома. Поэтому временный файл
/// создаётся рядом с целевым, а не во временном каталоге.
/// </remarks>
public static class AtomicFile
{
	private static readonly Lock replacement = new();

	public static Task<Result<Done>> WriteAsync(string path, string content, CancellationToken ct)
	{
		ArgumentNullException.ThrowIfNull(content);
		return WriteAsync(path, async (stream, token) =>
		{
			await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
			await writer.WriteAsync(content.AsMemory(), token).ConfigureAwait(false);
			await writer.FlushAsync(token).ConfigureAwait(false);
		}, ct);
	}

	/// <summary>Потоковая запись без сборки всего файла в одну строку.</summary>
	public static async Task<Result<Done>> WriteAsync(string path, Func<Stream, CancellationToken, Task> write, CancellationToken ct)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(write);

		var directory = Path.GetDirectoryName(path);
		var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");

		try
		{
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, useAsync: true))
			{
				await write(stream, ct).ConfigureAwait(false);
				await stream.FlushAsync(ct).ConfigureAwait(false);
			}

			for (var attempt = 0; ; attempt++)
			{
				try
				{
					// Подготовка содержимого параллельна; согласуется только замена.
					lock (replacement)
					{
						ct.ThrowIfCancellationRequested();
						if (!OperatingSystem.IsWindows() && File.Exists(path))
						{
							File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
						}
						File.Move(temporary, path, overwrite: true);
					}
					break;
				}
				catch (Exception exception) when (OperatingSystem.IsWindows() && attempt < 5
					&& exception is IOException or UnauthorizedAccessException
					&& (exception.HResult & 0xffff) is 5 or 32 or 33)
				{
					// Windows может ещё держать заменённый файл. Постоянный отказ после
					// ограниченных повторов возвращается вызывающему; отмена не теряется.
					await Task.Delay(TimeSpan.FromMilliseconds(20 * (1 << attempt)), ct).ConfigureAwait(false);
				}
			}
			return Done.Ok;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Не удалось записать {path}: {exception.Message}");
		}
		finally
		{
			TryDelete(temporary);
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Ошибка уборки не должна заменять исходную ошибку записи.
		}
	}
}
