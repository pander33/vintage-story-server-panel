using System.Text;
using Panel.Agent.Content;
using Panel.Fleet.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Provisioning;

/// <summary>Запись привилегированных файлов и очистка незарегистрированного дерева.</summary>
internal static class ProvisioningFiles
{
	private static readonly Lock replacement = new();

	/// <summary>Атомарная запись текста рядом с целью; права Unix ставятся там, где они есть.</summary>
	internal static async Task<Result<Done>> WriteAtomicallyAsync(string path, string content, UnixFileMode mode, CancellationToken ct)
	{
		var temporary = Path.Combine(
			Path.GetDirectoryName(path)!,
			$".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
		try
		{
			await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
			{
				await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
				await writer.WriteAsync(content.AsMemory(), ct).ConfigureAwait(false);
				await writer.FlushAsync(ct).ConfigureAwait(false);
			}

			lock (replacement)
			{
				ct.ThrowIfCancellationRequested();
				GameFiles.SetMode(temporary, mode);
				File.Move(temporary, path, overwrite: true);
			}
			return Done.Ok;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Cannot write {path}: {exception.Message}");
		}
		finally
		{
			try
			{
				File.Delete(temporary);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// Ошибка очистки не скрывает исходный отказ записи или отмену.
			}
		}
	}

	internal static void TryDelete(string directory)
	{
		try
		{
			if (Directory.Exists(directory))
			{
				Directory.Delete(directory, recursive: true);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Как ignore_errors=True: остаток виден оператору по пути в тексте отказа.
		}
	}
}
