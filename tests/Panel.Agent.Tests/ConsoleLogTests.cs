using System.Text;
using Panel.Agent.Supervision;

namespace Panel.Agent.Tests;

/// <summary>
/// Журнал консоли под супервизором: курсор — смещение в байтах, и чтение по нему не теряет и
/// не разрывает строки.
/// </summary>
public sealed class ConsoleLogTests : IDisposable
{
	private readonly string directory = Path.Combine(Path.GetTempPath(), "vs-console-" + Guid.NewGuid().ToString("n"));
	private readonly string log;

	public ConsoleLogTests()
	{
		Directory.CreateDirectory(directory);
		log = ConsoleLog.PathFor(directory, "main");
	}

	public void Dispose()
	{
		if (Directory.Exists(directory))
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	private void Append(string text) => File.AppendAllText(log, text, new UTF8Encoding(false));

	[Fact]
	public void Хвост_отдаёт_последние_строки_без_пустых_и_без_CR()
	{
		Append("первая\r\nвторая\r\n\r\nтретья\r\n");

		Assert.Equal(["вторая", "третья"], ConsoleLog.Tail(log, 2));
	}

	[Fact]
	public void Отсутствующий_журнал_пуст_а_не_ошибка()
	{
		Assert.Empty(ConsoleLog.Tail(log, 50));
		Assert.Null(ConsoleLog.End(log));
		Assert.Equal(new ConsoleChunk("0", []), ConsoleLog.After(log, "tail"));
	}

	[Fact]
	public void Tail_возвращает_конец_и_ничего_больше()
	{
		Append("старое\n");

		var chunk = ConsoleLog.After(log, "tail");

		Assert.Empty(chunk.Lines);
		Assert.Equal(new FileInfo(log).Length.ToString(), chunk.Cursor);
	}

	[Fact]
	public void После_курсора_только_новое()
	{
		Append("до команды\n");
		var cursor = ConsoleLog.End(log)!;
		Append("Ответ: 42\nЕщё строка\n");

		var chunk = ConsoleLog.After(log, cursor);

		Assert.Equal(["Ответ: 42", "Ещё строка"], chunk.Lines);
		Assert.Equal(new FileInfo(log).Length.ToString(), chunk.Cursor);
		Assert.Empty(ConsoleLog.After(log, chunk.Cursor).Lines);
	}

	/// <summary>
	/// Недописанная строка остаётся следующему чтению, и ответ команды не приходит по частям.
	/// </summary>
	[Fact]
	public void Недописанная_строка_ждёт_перевода_строки()
	{
		Append("готово\nполови");

		var first = ConsoleLog.After(log, "0");
		Append("на\n");
		var second = ConsoleLog.After(log, first.Cursor);

		Assert.Equal(["готово"], first.Lines);
		Assert.Equal(["половина"], second.Lines);
	}

	[Fact]
	public void Усечённый_журнал_читается_сначала()
	{
		Append(new string('x', 100) + "\n");
		var cursor = ConsoleLog.End(log)!;
		File.WriteAllText(log, "после усечения\n");

		Assert.Equal(["после усечения"], ConsoleLog.After(log, cursor).Lines);
	}

	[Theory]
	[InlineData("-5")]
	[InlineData("s=abc;i=1")]
	[InlineData("1e3")]
	public void Курсор_journald_здесь_недопустим(string cursor)
	{
		Append("строка\n");

		Assert.Throws<ArgumentException>(() => ConsoleLog.After(log, cursor));
	}

	/// <summary>Многобайтовый символ на границе смещения не разрывается: разбиение только по <c>\n</c>.</summary>
	[Fact]
	public void Смещение_считается_в_байтах()
	{
		Append("ёлка\n");
		var cursor = ConsoleLog.End(log)!;

		Assert.Equal(Encoding.UTF8.GetByteCount("ёлка\n").ToString(), cursor);
	}

	/// <summary>
	/// Журнал лежит вне <c>Logs</c> игры: при старте она переносит этот каталог в архив вместе с
	/// открытым журналом консоли.
	/// </summary>
	[Fact]
	public void Журнал_лежит_в_каталоге_состояния_а_не_в_журналах_игры()
	{
		Assert.Equal(Path.Combine(directory, "main.console.log"), log);
		Assert.Equal(Path.Combine(directory, "main.console.previous.log"), ConsoleLog.PreviousPathFor(directory, "main"));
	}

	/// <summary>
	/// Игра без консоли пишет в кодировке OEM, и строка не в UTF-8 читается в этой кодировке.
	/// </summary>
	[Fact]
	public void Строка_не_в_UTF8_читается_в_кодировке_OEM()
	{
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		var oem = Encoding.GetEncoding(ConsoleLog.OemCodePage);
		var text = oem.GetString(oem.GetBytes("Выделенный сервер now running"));

		File.WriteAllBytes(log, [.. oem.GetBytes(text + "\r\n"), .. Encoding.UTF8.GetBytes("ёлка\r\n")]);

		Assert.Equal([text, "ёлка"], ConsoleLog.Tail(log, 10));
	}

	/// <summary>
	/// Строка из журнала настоящего сервера на русской Windows. В режиме инвариантной
	/// глобализации страница OEM культуры 437, и строка читалась с искажениями. Проверяется
	/// там, где системная страница OEM 866.
	/// </summary>
	[Fact]
	public void Вывод_игры_на_русской_Windows_читается()
	{
		if (ConsoleLog.OemCodePage != 866)
		{
			return;
		}

		byte[] line = [.. Encoding.ASCII.GetBytes("[Server Event] "), 0x82, 0xEB, 0xA4, 0xA5, 0xAB, 0xA5, 0xAD, 0xAD, 0xEB, 0xA9, 0x20, 0xE1, 0xA5, 0xE0, 0xA2, 0xA5, 0xE0, .. Encoding.ASCII.GetBytes(" now running\r\n")];
		File.WriteAllBytes(log, line);

		Assert.Equal(["[Server Event] Выделенный сервер now running"], ConsoleLog.Tail(log, 10));
	}

	[Fact]
	public void Перед_запуском_прошлый_журнал_становится_предыдущим()
	{
		var previous = ConsoleLog.PreviousPathFor(directory, "main");
		File.WriteAllText(previous, "позапрошлый\n");
		Append("прошлый\n");

		ConsoleLog.Rotate(log, previous);

		Assert.False(File.Exists(log));
		Assert.Equal("прошлый\n", File.ReadAllText(previous));

		ConsoleLog.Rotate(log, previous);
		Assert.Equal("прошлый\n", File.ReadAllText(previous));
	}
}
