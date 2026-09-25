using System.Text;

namespace Panel.Agent.Supervision;

/// <summary>
/// Командная строка Windows из списка аргументов.
/// </summary>
/// <remarks>
/// На Windows процесс получает одну строку и сам разбирает её по правилам
/// <c>CommandLineToArgvW</c>. <c>ProcessStartInfo.ArgumentList</c> собирает строку сам, но
/// супервизор запускает сервер через <c>CreateProcessW</c> ради наследования дескриптора
/// журнала, поэтому строка собирается здесь.
///
/// Аргумент без пробелов и кавычек идёт как есть. Иначе он берётся в кавычки, кавычки внутри
/// экранируются, а обратные косые черты перед кавычкой (и перед закрывающей) удваиваются.
/// На путях вида <c>C:\Program Files\x\</c> простая склейка ломается.
/// </remarks>
public static class WindowsCommandLine
{
	public static string Build(IEnumerable<string> arguments)
	{
		ArgumentNullException.ThrowIfNull(arguments);
		return string.Join(' ', arguments.Select(Quote));
	}

	public static string Quote(string argument)
	{
		ArgumentNullException.ThrowIfNull(argument);

		if (argument.Contains('\0', StringComparison.Ordinal))
		{
			throw new ArgumentException("Аргумент содержит нулевой символ", nameof(argument));
		}

		if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
		{
			return argument;
		}

		var quoted = new StringBuilder(argument.Length + 2).Append('"');
		var backslashes = 0;

		foreach (var symbol in argument)
		{
			if (symbol == '\\')
			{
				backslashes++;
				continue;
			}

			if (symbol == '"')
			{
				quoted.Append('\\', backslashes * 2 + 1).Append('"');
			}
			else
			{
				quoted.Append('\\', backslashes).Append(symbol);
			}

			backslashes = 0;
		}

		return quoted.Append('\\', backslashes * 2).Append('"').ToString();
	}
}
