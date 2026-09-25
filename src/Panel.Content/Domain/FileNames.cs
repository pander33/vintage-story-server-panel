using System.Text.RegularExpressions;
using Panel.SharedKernel;

namespace Panel.Content.Domain;

/// <summary>
/// Имя файла мода: правило <c>SAFE_NAME</c> и обязательное расширение <c>.zip</c>.
/// </summary>
/// <remarks>
/// Имя мода приходит из тела запроса и проверяется типом, поэтому непроверенное имя в
/// сигнатуру не попадёт.
/// </remarks>
public readonly partial record struct ModArchiveName
{
	private readonly string? value;

	private ModArchiveName(string value) => this.value = value;

	public string Value => value ?? throw new InvalidOperationException("Имя мода не задано");

	public bool IsEmpty => value is null;

	/// <summary><c>SAFE_NAME</c>: пробел и точка внутри разрешены, в начале точка запрещена.</summary>
	[GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+ -]{0,199}$")]
	private static partial Regex Pattern { get; }

	public static Result<ModArchiveName> Create(string? raw)
	{
		var value = (raw ?? string.Empty).Trim();

		if (!Pattern.IsMatch(value))
		{
			return Failure.Invalid("Недопустимое имя файла мода");
		}

		return value.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
			? new ModArchiveName(value)
			: Failure.Invalid("Мод должен быть ZIP-архивом");
	}

	public static ModArchiveName Parse(string raw) => Names.OrThrow(Create(raw), nameof(raw));

	public override string ToString() => value ?? string.Empty;
}

/// <summary>
/// Имя файла мира: не <c>.</c> и не <c>..</c>, без разделителей пути и управляющих символов, не
/// длиннее 200 символов, расширение <c>.vcdbs</c> обязательно.
/// </summary>
/// <remarks>
/// Набор символов шире, чем у мода: допускается кириллица, потому что миры с русскими именами
/// уже существуют.
/// </remarks>
public readonly record struct WorldFileName
{
	public const int MaxLength = 200;
	public const string Extension = ".vcdbs";

	private readonly string? value;

	private WorldFileName(string value) => this.value = value;

	public string Value => value ?? throw new InvalidOperationException("Имя мира не задано");

	public bool IsEmpty => value is null;

	public static Result<WorldFileName> Create(string? raw)
	{
		var value = (raw ?? string.Empty).Trim();

		if (!Names.IsPlainFileName(value) || value.Length > MaxLength)
		{
			return Failure.Invalid("Недопустимое имя файла мира");
		}

		return value.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
			? new WorldFileName(value)
			: Failure.Invalid("Файл мира должен иметь расширение .vcdbs");
	}

	/// <summary>
	/// Имя из формы создания мира; расширение дописывается, если его нет.
	/// </summary>
	public static Result<WorldFileName> CreateAddingExtension(string? raw)
	{
		var value = (raw ?? string.Empty).Trim();

		return Create(value.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
			? value
			: value + Extension);
	}

	public static WorldFileName Parse(string raw) => Names.OrThrow(Create(raw), nameof(raw));

	public override string ToString() => value ?? string.Empty;
}

/// <summary>
/// Имя мира в <c>.archive</c> или <c>.trash</c>: метка времени и исходное имя. Расширение
/// <c>.vcdbs</c> обязательно, по нему хранилище ищет файлы.
/// </summary>
public readonly partial record struct StoredWorldName
{
	private readonly string? value;

	private StoredWorldName(string value) => this.value = value;

	public string Value => value ?? throw new InvalidOperationException("Имя архивного мира не задано");

	public bool IsEmpty => value is null;

	/// <summary>Разбор метки времени перед исходным именем.</summary>
	[GeneratedRegex(@"^\d{8}-\d{6}(?:-\d+)?-(?<original>.+\.vcdbs)$", RegexOptions.IgnoreCase)]
	private static partial Regex StampedPattern { get; }

	public static Result<StoredWorldName> Create(string? raw)
	{
		var value = (raw ?? string.Empty).Trim();

		if (!Names.IsPlainFileName(value) || value.Length > WorldFileName.MaxLength + 32)
		{
			return Failure.Invalid("Недопустимое имя архивного мира");
		}

		return value.EndsWith(WorldFileName.Extension, StringComparison.OrdinalIgnoreCase)
			? new StoredWorldName(value)
			: Failure.Invalid("Архивный мир должен иметь расширение .vcdbs");
	}

	/// <summary>
	/// Имя мира до архивации. Если метки времени нет, имя возвращается как есть.
	/// </summary>
	public string OriginalName =>
		StampedPattern.Match(Value) is { Success: true } match
			? match.Groups["original"].Value
			: Value;

	public static StoredWorldName Parse(string raw) => Names.OrThrow(Create(raw), nameof(raw));

	public override string ToString() => value ?? string.Empty;
}

/// <summary>
/// Имя файла резервной копии. Приставка важна: <c>vintagestory-</c> — обычная копия,
/// <c>before-restore-</c> — страховочная, снятая перед восстановлением.
/// </summary>
/// <remarks>
/// Политика удержания удаляет только обычные копии, поэтому вид копии хранится в типе, а не
/// определяется сравнением строк в разных местах. Страховочная копия нужна на случай
/// неудачного восстановления и по расписанию не удаляется.
/// </remarks>
public readonly partial record struct BackupName
{
	public const string RoutinePrefix = "vintagestory-";
	public const string SafetyPrefix = "before-restore-";

	private readonly string? value;

	private BackupName(string value) => this.value = value;

	public string Value => value ?? throw new InvalidOperationException("Имя резервной копии не задано");

	public bool IsEmpty => value is null;

	/// <summary>Обычная ли это копия; страховочные политика удержания не трогает.</summary>
	public bool IsRoutine => Value.StartsWith(RoutinePrefix, StringComparison.Ordinal);

	[GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+ -]{0,199}$")]
	private static partial Regex Pattern { get; }

	public static Result<BackupName> Create(string? raw)
	{
		var value = (raw ?? string.Empty).Trim();

		if (!Pattern.IsMatch(value) || !Names.IsPlainFileName(value))
		{
			return Failure.Invalid("Недопустимое имя резервной копии");
		}

		if (!value.EndsWith(".tar.gz", StringComparison.Ordinal))
		{
			return Failure.Invalid("Резервная копия должна быть архивом .tar.gz");
		}

		return value.StartsWith(RoutinePrefix, StringComparison.Ordinal)
			|| value.StartsWith(SafetyPrefix, StringComparison.Ordinal)
				? new BackupName(value)
				: Failure.Invalid("Резервная копия должна быть создана панелью");
	}

	public static BackupName Parse(string raw) => Names.OrThrow(Create(raw), nameof(raw));

	public override string ToString() => value ?? string.Empty;
}

/// <summary>Имя файла настроек мода: <c>SAFE_NAME</c> и расширение <c>.json</c>.</summary>
public readonly partial record struct ModConfigName
{
	private readonly string? value;

	private ModConfigName(string value) => this.value = value;

	public string Value => value ?? throw new InvalidOperationException("Имя файла настроек не задано");

	public bool IsEmpty => value is null;

	[GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+ -]{0,199}$")]
	private static partial Regex Pattern { get; }

	public static Result<ModConfigName> Create(string? raw)
	{
		var value = (raw ?? string.Empty).Trim();

		if (!Pattern.IsMatch(value) || !Names.IsPlainFileName(value))
		{
			return Failure.Invalid("Недопустимое имя файла настроек мода");
		}

		return value.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
			? new ModConfigName(value)
			: Failure.Invalid("Настройки мода хранятся в файле .json");
	}

	public static ModConfigName Parse(string raw) => Names.OrThrow(Create(raw), nameof(raw));

	public override string ToString() => value ?? string.Empty;
}

/// <summary>Общие проверки имён; снаружи видны только сами типы имён.</summary>
internal static class Names
{
	/// <summary>Правило общее с разделяемым ядром, см. <see cref="PlainFileName"/>.</summary>
	public static bool IsPlainFileName(string value) => PlainFileName.IsPlain(value);

	public static T OrThrow<T>(Result<T> result, string parameter) =>
		result.IsSuccess ? result.Value : throw new ArgumentException(result.Failure!.Message, parameter);
}
