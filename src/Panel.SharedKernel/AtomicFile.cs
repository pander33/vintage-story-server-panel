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
	public static async Task<Result<Done>> WriteAsync(string path, string content, CancellationToken ct)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(content);

		var directory = Path.GetDirectoryName(path);
		var temporary = path + ".tmp." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

		try
		{
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), ct).ConfigureAwait(false);

			if (!OperatingSystem.IsWindows() && File.Exists(path))
			{
				// Права целевого файла сохраняются: secret.key и accounts.json лежат с правами 0600, и
				// запись не должна их ослаблять.
				File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
			}

			File.Move(temporary, path, overwrite: true);
			return Done.Ok;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			TryDelete(temporary);
			return Failure.Unavailable($"Не удалось записать {path}: {exception.Message}");
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
