using System.Globalization;
using Panel.SharedKernel;

namespace Panel.Contracts.ServerCommands;

/// <summary>
/// Область допустимых значений параметра команды.
/// </summary>
/// <remarks>
/// Таблица команд одна, а <see cref="Validate"/> вызывается дважды: в Web, чтобы отказать
/// раньше, и в Agent, который не доверяет веб-процессу и вызывает свою скомпилированную копию,
/// а не присланное описание.
///
/// Области значений описаны полностью: например, <c>time-set</c> принимает и 11 пресетов, и
/// час от 0 до 23.99, и интерфейс видит оба варианта.
/// </remarks>
public abstract record ValueDomain
{
	private ValueDomain()
	{
	}

	/// <summary>Предел длины значения, как в <c>safe_text(value, 32, …)</c>.</summary>
	public const int MaxLength = 32;

	public abstract Result<string> Validate(string? raw);

	/// <summary>Выбор из закрытого списка без учёта регистра.</summary>
	public sealed record Choice(IReadOnlyList<string> Options, Phrase Label) : ValueDomain
	{
		public override Result<string> Validate(string? raw)
		{
			var normalized = Normalize(raw);
			if (!normalized.IsSuccess)
			{
				return normalized;
			}

			var value = normalized.Value;
			var match = Options.FirstOrDefault(
				option => string.Equals(option, value, StringComparison.OrdinalIgnoreCase));

			return match ?? (Result<string>) Failure.Invalid($"Недопустимое значение: {Label}");
		}
	}

	/// <summary>Число в пределах с заданным шагом и числом знаков после запятой.</summary>
	public sealed record Number(decimal Min, decimal Max, decimal Step, int Decimals, Phrase Label)
		: ValueDomain
	{
		/// <summary>
		/// Начальное значение поля при отрисовке, только для интерфейса; на проверку не влияет. Без
		/// него фронтенд ставит <c>0</c>, и скорость времени по умолчанию превращается в остановку.
		/// </summary>
		public string? Default { get; init; }

		public override Result<string> Validate(string? raw)
		{
			var normalized = Normalize(raw);
			if (!normalized.IsSuccess)
			{
				return normalized;
			}

			var value = normalized.Value;
			if (!decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
					CultureInfo.InvariantCulture, out var number))
			{
				return Failure.Invalid($"{Label}: требуется число");
			}

			if (number < Min || number > Max)
			{
				return Failure.Invalid($"{Label}: требуется значение от {Min} до {Max}");
			}

			var fraction = value.Split('.');
			return fraction.Length > 1 && fraction[1].Length > Decimals
				? Failure.Invalid($"{Label}: не более {Decimals} знаков после запятой")
				: value;
		}
	}

	/// <summary>
	/// Время суток: пресет или час от 0 до 23.99.
	/// </summary>
	/// <remarks>
	/// Оба варианта описаны здесь, и интерфейс может нарисовать и список, и поле ввода часа.
	/// </remarks>
	public sealed record TimeOfDay(IReadOnlyList<string> Presets, Phrase Label) : ValueDomain
	{
		public override Result<string> Validate(string? raw)
		{
			var normalized = Normalize(raw);
			if (!normalized.IsSuccess)
			{
				return normalized;
			}

			var value = normalized.Value;
			var preset = Presets.FirstOrDefault(
				option => string.Equals(option, value, StringComparison.OrdinalIgnoreCase));
			if (preset is not null)
			{
				return preset;
			}

			var parts = value.Split('.');
			var validHour = parts.Length <= 2
				&& int.TryParse(parts[0], CultureInfo.InvariantCulture, out var hour)
				&& hour is >= 0 and <= 23
				&& (parts.Length == 1 || (parts[1].Length is 1 or 2 && parts[1].All(char.IsAsciiDigit)));

			return validHour
				? value
				: Failure.Invalid($"{Label}: пресет либо час от 0 до 23.99");
		}
	}

	/// <summary>
	/// Слишком длинное значение отвергается, а не обрезается: обрезка превратила бы неверный
	/// ввод в допустимую команду.
	/// </summary>
	private static Result<string> Normalize(string? raw)
	{
		var value = (raw ?? string.Empty).Trim();
		return value.Length <= MaxLength
			? value
			: Failure.Invalid($"Значение длиннее {MaxLength} символов");
	}
}
