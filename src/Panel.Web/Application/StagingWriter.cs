using System.Security.Cryptography;
using Panel.Content.Domain;
using Panel.SharedKernel;

namespace Panel.Web.Application;

/// <summary>
/// Запись загруженного файла в каталог промежуточного хранения.
/// </summary>
/// <remarks>
/// У веб-процесса нет доступа к игровым каталогам. Загруженный файл он кладёт сюда и сообщает
/// демону его имя. Предел размера проверяется во время записи, а не по заголовку
/// <c>Content-Length</c>, который присылает клиент.
/// </remarks>
public sealed class StagingWriter
{
	private readonly PanelOptions options;

	public StagingWriter(PanelOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		this.options = options;
	}

	/// <summary>Новое свободное имя со случайной частью.</summary>
	public static StagedFile Reserve(string purpose, string extension) =>
		StagedFile.CreateFresh(purpose, extension, Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant());

	public string PathOf(StagedFile staged) => Path.Combine(options.StagingDirectory, staged.Name);

	/// <summary>
	/// Записать поток, не превысив предела. При превышении файл удаляется, чтобы оборванная
	/// загрузка не занимала место до перезапуска панели.
	/// </summary>
	/// <param name="tooLarge">Текст отказа при превышении; для мира и пакета сервера свой.</param>
	public async Task<Result<StagedFile>> WriteAsync(
		Stream source,
		StagedFile staged,
		int limitMegabytes,
		CancellationToken ct,
		Phrase? tooLarge = null)
	{
		Directory.CreateDirectory(options.StagingDirectory);
		var path = PathOf(staged);
		var limit = (long) limitMegabytes * 1024 * 1024;
		var written = 0L;

		try
		{
			await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
			{
				var buffer = new byte[1024 * 1024];
				int read;

				while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
				{
					written += read;
					if (written > limit)
					{
						return Failure.TooLarge(tooLarge ?? Phrase.Format($"Файл больше {limitMegabytes} МБ"));
					}

					await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
				}
			}

			if (OperatingSystem.IsLinux())
			{
				File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
			}

			return staged;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Не удалось принять файл: {exception.Message}");
		}
		finally
		{
			if (written > limit && File.Exists(path))
			{
				File.Delete(path);
			}
		}
	}

	/// <summary>Удалить временный файл; вызывается и при успехе, и при отказе.</summary>
	public void Discard(StagedFile staged)
	{
		try
		{
			File.Delete(PathOf(staged));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// Неудалённый временный файл не повод отказывать в операции, которая уже выполнена.
		}
	}
}
