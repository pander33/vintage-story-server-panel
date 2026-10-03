using System.Text.RegularExpressions;
using Panel.SharedKernel;

namespace Panel.Contracts.ServerCommands;

/// <summary>
/// Изменение живой конфигурации работающего мира: <c>/worldconfig</c> или
/// <c>/worldconfigcreate</c>.
/// </summary>
/// <remarks>
/// <para>
/// Живая <c>World.Config</c> — не шаблон из <c>serverconfig.json</c>. Она меняется
/// только этими двумя командами и только в разрешённой форме: имя — латинский
/// идентификатор, тип — один из пяти, значение — без управляющих символов и проверено по
/// типу. Строка команды собирается здесь, а не приходит целиком.
/// </para>
/// <para>
/// Правило лежит в контрактах, как и каталог консольных действий: им пользуются обе стороны.
/// Веб-процесс отвергает неверное до вызова демона, демон проверяет ещё раз.
/// </para>
/// </remarks>
public sealed partial record WorldConfigCommand
{
	private const int maxValueLength = 500;

	private static readonly string[] actions = ["set", "create"];

	private static readonly string[] dataTypes = ["bool", "int", "float", "double", "string"];

	private WorldConfigCommand(string action, string dataType, string name, string value)
	{
		Action = action;
		DataType = dataType;
		Name = name;
		Value = value;
	}

	public string Action { get; }

	public string DataType { get; }

	public string Name { get; }

	public string Value { get; }

	/// <summary>
	/// Разбор и проверка. Тексты отказов по-русски, потому что показываются в интерфейсе.
	/// </summary>
	public static Result<WorldConfigCommand> Create(string? action, string? dataType, string? name, string? value)
	{
		if (!actions.Contains(action, StringComparer.Ordinal) || !dataTypes.Contains(dataType, StringComparer.Ordinal))
		{
			return Failure.Invalid("Неподдерживаемое действие с настройкой мира");
		}

		if (name is null || !NamePattern().IsMatch(name))
		{
			return Failure.Invalid("Недопустимое имя настройки мира");
		}

		// Обрезка пробелов, проверка длины и отсутствия символов с кодом меньше пробела. Пустое
		// значение допустимо только для строки.
		var text = (value ?? string.Empty).Trim();
		if ((text.Length == 0 && dataType != "string")
			|| text.EnumerateRunes().Count() > maxValueLength
			|| text.Any(symbol => symbol < ' '))
		{
			return Failure.Invalid("Недопустимое значение настройки мира");
		}

		var typed = dataType switch
		{
			"bool" when !text.Equals("true", StringComparison.OrdinalIgnoreCase)
						&& !text.Equals("false", StringComparison.OrdinalIgnoreCase) =>
				Failure.Invalid("Логическое значение должно быть true или false"),
			"int" when !IntegerPattern().IsMatch(text) =>
				Failure.Invalid("Недопустимое целое значение"),
			"float" or "double" when !RealPattern().IsMatch(text) =>
				Failure.Invalid("Недопустимое число; дробную часть отделяйте точкой"),
			_ => null,
		};

		return typed is null ? new WorldConfigCommand(action!, dataType!, name, text) : typed;
	}

	/// <summary>Строка для консоли игры.</summary>
	public string Render() => Action == "set"
		? $"/worldconfig {Name} {Value}"
		: $"/worldconfigcreate {DataType} {Name} {Value}";

	/// <summary>Аргументы операции демона в фиксированном порядке.</summary>
	public IReadOnlyList<string> ToArguments() => [Action, DataType, Name, Value];

	[GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]{0,79}\z", RegexOptions.CultureInvariant)]
	private static partial Regex NamePattern();

	[GeneratedRegex(@"^-?[0-9]{1,18}\z", RegexOptions.CultureInvariant)]
	private static partial Regex IntegerPattern();

	[GeneratedRegex(@"^-?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)\z", RegexOptions.CultureInvariant)]
	private static partial Regex RealPattern();
}
