using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;

namespace Panel.SharedKernel;

/// <summary>
/// Текст для человека: русский шаблон с местами <c>{0}</c>, <c>{1}</c> и значения для них.
/// </summary>
/// <remarks>
/// <para>
/// Русский текст — <see cref="ToString"/>; он пишется в журналы и файлы состояния. Язык
/// интерфейса выбирает граница HTTP: переводит шаблон по каталогу и подставляет те же значения.
/// Поэтому шаблон — ключ каталога, и собирать сообщение склейкой строк нельзя: перевод потеряет
/// его. Интерполированная строка (<c>$"Порт {port} уже занят"</c>) превращается в шаблон сама,
/// через <see cref="PhraseHandler"/>.
/// </para>
/// <para>
/// Значение — строка или вложенная <see cref="Phrase"/> (например, причина отказа внутри
/// другого отказа): вложенная переводится тоже.
/// </para>
/// <para>
/// Неявного преобразования из строки нет: фраза создаётся явно (<see cref="From"/>,
/// <see cref="Format"/>) или фабрикой отказа, и проверка полноты каталога находит каждый
/// шаблон в исходниках. В JSON — <see cref="PhraseJsonConverter"/>.
/// </para>
/// </remarks>
[JsonConverter(typeof(PhraseJsonConverter))]
public sealed class Phrase : IEquatable<Phrase>
{
	private string? text;

	public Phrase(string template, IReadOnlyList<object> args)
	{
		Template = template;
		Args = args;
	}

	/// <summary>Шаблон в синтаксисе <see cref="string.Format(string, object?[])"/>: фигурные скобки текста удвоены.</summary>
	public string Template { get; }

	/// <summary>Значения мест шаблона: <see cref="string"/> или <see cref="Phrase"/>.</summary>
	public IReadOnlyList<object> Args { get; }

	/// <summary>Готовый текст без значений: он сам себе шаблон.</summary>
	public static Phrase From(string text) => new(Escape(text), []);

	public static Phrase Format(ref PhraseHandler handler) => handler.ToPhrase();

	/// <summary>Текст на языке шаблона с подставленными значениями.</summary>
	public override string ToString() => text ??= Render(Template, Args.Select(arg => arg.ToString() ?? string.Empty));

	/// <summary>Шаблон с подставленными значениями: общий путь для любого языка.</summary>
	public static string Render(string template, IEnumerable<string> values)
	{
		var args = values.ToArray<object>();
		return args.Length == 0
			? template.Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal)
			: string.Format(CultureInfo.InvariantCulture, template, args);
	}

	public static string Escape(string text) =>
		text.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);

	public bool Equals(Phrase? other) =>
		other is not null && Template == other.Template && ToString() == other.ToString();

	public override bool Equals(object? obj) => Equals(obj as Phrase);

	public override int GetHashCode() => ToString().GetHashCode(StringComparison.Ordinal);
}

/// <summary>
/// Собирает <see cref="Phrase"/> из интерполированной строки: текст — в шаблон, выражения — в
/// значения. Числа и даты форматируются инвариантно. Не ref struct: сообщение собирают и
/// в асинхронных методах, где рядом стоит await.
/// </summary>
[InterpolatedStringHandler]
public struct PhraseHandler
{
	private readonly StringBuilder template;
	private readonly List<object> args;

	public PhraseHandler(int literalLength, int formattedCount)
	{
		template = new StringBuilder(literalLength + (formattedCount * 3));
		args = new List<object>(formattedCount);
	}

	public readonly void AppendLiteral(string value) => template.Append(Phrase.Escape(value));

	public readonly void AppendFormatted<T>(T value) => Append(value, 0, null);

	public readonly void AppendFormatted<T>(T value, string? format) => Append(value, 0, format);

	public readonly void AppendFormatted<T>(T value, int alignment) => Append(value, alignment, null);

	public readonly void AppendFormatted<T>(T value, int alignment, string? format) => Append(value, alignment, format);

	public readonly Phrase ToPhrase() => new(template.ToString(), args.ToArray());

	private readonly void Append<T>(T value, int alignment, string? format)
	{
		template.Append('{').Append(args.Count.ToString(CultureInfo.InvariantCulture)).Append('}');
		object argument = value switch
		{
			Phrase phrase => phrase,
			Failure failure => failure.Text,
			IFormattable formattable => formattable.ToString(format, CultureInfo.InvariantCulture),
			null => string.Empty,
			_ => value.ToString() ?? string.Empty,
		};

		if (alignment != 0 && argument is string text)
		{
			argument = alignment > 0 ? text.PadLeft(alignment) : text.PadRight(-alignment);
		}

		args.Add(argument);
	}
}
