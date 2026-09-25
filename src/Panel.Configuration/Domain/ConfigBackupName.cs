using System.Text.RegularExpressions;
using Panel.SharedKernel;

namespace Panel.Configuration.Domain;

/// <summary>
/// Имя резервной копии настроек: <c>serverconfig.json.panel-20260921-143000.bak</c>, при
/// совпадении — с числом <c>-1</c>, <c>-2</c> перед расширением.
/// </summary>
/// <remarks>
/// Веб-процесс знает только общую форму: имени файла конфигурации он не знает, путь выводит
/// демон из реестра. Поэтому проверок две: <see cref="Create"/> на стороне панели и
/// <see cref="CreateFor"/> на стороне демона, который ещё проверяет, что копия снята именно с
/// этого файла.
/// </remarks>
public readonly record struct ConfigBackupName
{
	private const string suffix = @"\.panel-\d{8}-\d{6}(?:-\d+)?\.bak\z";

	private static readonly Regex shape = new(@"^.{1,200}" + suffix, RegexOptions.CultureInvariant);

	private ConfigBackupName(string value) => Value = value;

	public string Value { get; }

	public static Result<ConfigBackupName> Create(string? raw)
	{
		var value = (raw ?? string.Empty).Trim();

		return PlainFileName.IsPlain(value) && shape.IsMatch(value)
			? new ConfigBackupName(value)
			: Failure.Invalid("Недопустимое имя резервной копии настроек");
	}

	/// <summary>Имя копии, снятой именно с файла <paramref name="configFileName"/>.</summary>
	public static Result<ConfigBackupName> CreateFor(string? raw, string configFileName)
	{
		ArgumentException.ThrowIfNullOrEmpty(configFileName);

		var value = (raw ?? string.Empty).Trim();
		var pattern = new Regex("^" + Regex.Escape(configFileName) + suffix, RegexOptions.CultureInvariant);

		return PlainFileName.IsPlain(value) && pattern.IsMatch(value)
			? new ConfigBackupName(value)
			: Failure.Invalid("Недопустимое имя резервной копии настроек");
	}

	public override string ToString() => Value ?? string.Empty;
}
