namespace Panel.Cli;

/// <summary>Удаление файлов, которых может уже не быть.</summary>
internal static class Files
{
	/// <summary>
	/// Удалить файл, если он есть. <c>File.Delete</c> молчит об отсутствующем файле, но бросает
	/// <see cref="DirectoryNotFoundException"/>, если нет каталога: так падал
	/// <c>vs-panel uninstall</c> на машине без <c>/etc/sudoers.d</c>. Висящую ссылку
	/// <c>File.Exists</c> не видит, а <c>File.Delete</c> удаляет, поэтому проверки заранее нет.
	/// </summary>
	public static void Delete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (DirectoryNotFoundException)
		{
			// Нет каталога — нет и файла.
		}
	}
}
