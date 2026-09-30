using System.Globalization;
using System.Text;

namespace Panel.Agent.Supervision;

/// <summary>
/// Журнал консоли сервера под супервизором, <c>&lt;run_dir&gt;/&lt;id&gt;.console.log</c>:
/// сюда дочерний процесс пишет stdout и stderr. Замена journald на Windows.
/// </summary>
/// <remarks>
/// <para>
/// Курсор — смещение в байтах десятичным числом. Панель его не разбирает, только возвращает,
/// как и курсор journald.
/// </para>
/// <para>
/// Журнал лежит в каталоге <c>run</c>, а не в <c>Logs</c>: при старте игра переносит всё из
/// <c>Logs</c> в <c>Logs/Archive/&lt;время&gt;</c> вместе с открытым журналом, и панель читала
/// бы пустой файл. Журнал прошлого запуска хранится рядом (<see cref="PreviousPathFor"/>),
/// чтобы после перезапуска была видна причина падения.
/// </para>
/// </remarks>
public static class ConsoleLog
{
	/// <summary>Сколько байт читать за раз с конца или от курсора.</summary>
	public const int ChunkBytes = 512 * 1024;

	public static string PathFor(string runDirectory, string instance) =>
		Path.Combine(runDirectory, instance + ".console.log");

	public static string PreviousPathFor(string runDirectory, string instance) =>
		Path.Combine(runDirectory, instance + ".console.previous.log");

	/// <summary>
	/// Перед запуском текущий журнал становится предыдущим, прежний предыдущий удаляется. Если
	/// файл занят, новый запуск допишет в тот же журнал.
	/// </summary>
	public static void Rotate(string current, string previous)
	{
		try
		{
			if (File.Exists(current))
			{
				File.Move(current, previous, overwrite: true);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
		}
	}

	/// <summary>Последние строки. Нечитаемый журнал даёт пустой список, а не отказ.</summary>
	public static IReadOnlyList<string> Tail(string path, int count)
	{
		byte[] data;
		try
		{
			using var stream = Open(path);
			var start = Math.Max(0, stream.Length - ChunkBytes);
			stream.Seek(start, SeekOrigin.Begin);
			data = ReadUpTo(stream, (int) (stream.Length - start));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return [];
		}

		var lines = Split(data);
		return count <= 0 ? [] : [.. lines.Skip(Math.Max(0, lines.Count - count))];
	}

	/// <summary>Конец журнала: курсор, после которого пойдут новые строки.</summary>
	public static string? End(string path)
	{
		try
		{
			return new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>
	/// Всё, что появилось после курсора. Для <c>tail</c> только текущий конец.
	/// </summary>
	/// <remarks>
	/// Недописанная последняя строка остаётся следующему чтению, чтобы ответ команды не пришёл
	/// двумя кусками. Исключение — порция целиком без перевода строки: её приходится отдать,
	/// иначе чтение не сдвинется.
	///
	/// Если журнал стал короче курсора, его усекли или заменили, и чтение начинается сначала.
	/// </remarks>
	public static ConsoleChunk After(string path, string cursor)
	{
		long size;
		try
		{
			size = new FileInfo(path).Length;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return new ConsoleChunk("0", []);
		}

		if (cursor is "tail" or "")
		{
			return new ConsoleChunk(size.ToString(CultureInfo.InvariantCulture), []);
		}

		if (!long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
		{
			throw new ArgumentException("Недопустимое смещение в журнале консоли", nameof(cursor));
		}

		if (offset > size)
		{
			offset = 0;
		}

		// Буфер по тому, что появилось после курсора, а не на всю порцию: консоль опрашивается
		// каждые полторы секунды, и полмегабайта на каждый опрос ради пары строк были бы мусором.
		byte[] data;
		try
		{
			using var stream = Open(path);
			stream.Seek(offset, SeekOrigin.Begin);
			data = ReadUpTo(stream, (int) Math.Min(ChunkBytes, stream.Length - offset));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return new ConsoleChunk(size.ToString(CultureInfo.InvariantCulture), []);
		}

		var usable = data.Length;
		var lastNewline = Array.LastIndexOf(data, (byte) '\n');
		if (lastNewline >= 0)
		{
			usable = lastNewline + 1;
		}
		else if (data.Length < ChunkBytes)
		{
			usable = 0;
		}

		var next = offset + usable;
		return new ConsoleChunk(next.ToString(CultureInfo.InvariantCulture), Split(data.AsSpan(0, usable)));
	}

	/// <summary>
	/// Журнал читается, пока дочерний процесс держит его открытым на запись, поэтому доступ
	/// разделяется и на запись, и на удаление.
	/// </summary>
	private static FileStream Open(string path) =>
		new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

	private static byte[] ReadUpTo(Stream stream, int limit)
	{
		var buffer = new byte[Math.Max(0, limit)];
		var total = 0;
		while (total < buffer.Length)
		{
			var read = stream.Read(buffer, total, buffer.Length - total);
			if (read == 0)
			{
				break;
			}

			total += read;
		}

		return total == buffer.Length ? buffer : buffer[..total];
	}

	/// <summary>Непустые строки; CRLF, который игра пишет на Windows, отбрасывается.</summary>
	private static List<string> Split(ReadOnlySpan<byte> data)
	{
		var lines = new List<string>();
		var start = 0;
		while (start <= data.Length)
		{
			var found = data[start..].IndexOf((byte) '\n');
			var end = found < 0 ? data.Length : start + found;

			var line = Decode(data[start..end]).TrimEnd('\r');
			if (line.Trim().Length > 0)
			{
				lines.Add(line);
			}

			start = end + 1;
		}

		return lines;
	}

	private static readonly Encoding strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	/// <summary>
	/// Строка декодируется как UTF-8, а если не получается — в кодовой странице OEM системы.
	/// Игра без консоли пишет в файл в OEM (на русской Windows 866), и «Выделенный сервер»
	/// превращался в кракозябры. Решение принимается для каждой строки: в журнале смешаны
	/// вывод dotnet и вывод игры.
	/// </summary>
	/// <remarks>
	/// Страница берётся системная (<c>GetOEMCP</c>), а не из культуры: в режиме инвариантной
	/// глобализации страница OEM культуры всегда 437. Игра пишет в системной странице
	/// независимо от учётной записи.
	/// </remarks>
	public static string Decode(ReadOnlySpan<byte> line)
	{
		try
		{
			return strictUtf8.GetString(line);
		}
		catch (DecoderFallbackException)
		{
			return oem.Value.GetString(line);
		}
	}

	/// <summary>Кодовая страница OEM системы; не на Windows — страница культуры.</summary>
	public static int OemCodePage =>
		OperatingSystem.IsWindows() ? (int) GetOEMCP() : CultureInfo.CurrentCulture.TextInfo.OEMCodePage;

	[System.Runtime.InteropServices.DllImport("kernel32.dll")]
	private static extern uint GetOEMCP();

	private static readonly Lazy<Encoding> oem = new(() =>
	{
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		try
		{
			return Encoding.GetEncoding(OemCodePage);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			return Encoding.Latin1;
		}
	});
}

/// <summary>Порция журнала консоли и курсор, с которого продолжать.</summary>
public sealed record ConsoleChunk(string Cursor, IReadOnlyList<string> Lines);
