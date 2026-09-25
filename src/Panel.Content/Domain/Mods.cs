using System.Text.RegularExpressions;
using Panel.SharedKernel;

namespace Panel.Content.Domain;

/// <summary>
/// Идентификатор мода из <c>modinfo.json</c> в нижнем регистре.
/// </summary>
/// <remarks>
/// Регистр приводится один раз при создании, поэтому все сравнения и каталог истории
/// <c>Mods/.history/&lt;modid&gt;</c> используют одно и то же значение.
/// </remarks>
public readonly partial record struct ModId
{
	private readonly string? value;

	private ModId(string value) => this.value = value;

	public string Value => value ?? throw new InvalidOperationException("Идентификатор мода не задан");

	public bool IsEmpty => value is null;

	/// <summary><c>SAFE_MOD_ID</c>: идентификатор становится именем каталога.</summary>
	[GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
	private static partial Regex Pattern { get; }

	public static Result<ModId> Create(string? raw)
	{
		var value = (raw ?? string.Empty).Trim().ToLowerInvariant();

		return Pattern.IsMatch(value)
			? new ModId(value)
			: Failure.Invalid("Недопустимый идентификатор мода");
	}

	public static ModId Parse(string raw) => Names.OrThrow(Create(raw), nameof(raw));

	public override string ToString() => value ?? string.Empty;
}

/// <summary>
/// Установленный мод: файл в <c>Mods</c> или <c>Mods/.disabled</c> вместе с разобранным
/// <c>modinfo.json</c>.
/// </summary>
/// <param name="File">Имя файла архива.</param>
/// <param name="Enabled">Лежит в <c>Mods</c>, а не в <c>Mods/.disabled</c>.</param>
/// <param name="Size">Размер архива в байтах.</param>
/// <param name="Modified">Время изменения файла.</param>
/// <param name="Id">Идентификатор из <c>modinfo.json</c>; пуст, если файл не разобран.</param>
/// <param name="Version">Версия из <c>modinfo.json</c>.</param>
/// <param name="DisplayName">Имя для интерфейса.</param>
/// <param name="Side">Сторона: <c>server</c>, <c>client</c>, <c>universal</c>.</param>
/// <param name="Dependencies">Зависимости: идентификатор → требуемая версия.</param>
/// <param name="Unreadable">Архив или <c>modinfo.json</c> не разобраны.</param>
public sealed record InstalledMod(
	ModArchiveName File,
	bool Enabled,
	long Size,
	DateTimeOffset Modified,
	ModId Id,
	string Version,
	string DisplayName,
	string Side,
	IReadOnlyDictionary<string, string> Dependencies,
	bool Unreadable)
{
	/// <summary>Ключ сортировки: идентификатор, а если <c>modinfo.json</c> не прочитан, имя файла.</summary>
	public string SortKey => (Id.IsEmpty ? File.Value : Id.Value).ToLowerInvariant();
}

/// <summary>
/// Все установленные моды. Агрегат, потому что дубликаты и недостающие зависимости —
/// свойства набора: файл сам по себе корректен и конфликтует только рядом с таким же.
/// </summary>
public sealed class ModCatalogue
{
	/// <summary>
	/// Моды, встроенные в игру. Их <c>modinfo.json</c> на диске нет, и без этого списка каждый
	/// мод, зависящий от <c>game</c>, выглядел бы сломанным.
	/// </summary>
	private static readonly string[] builtIn = ["game", "creative", "survival"];

	private readonly List<InstalledMod> mods;

	private ModCatalogue(List<InstalledMod> mods) => this.mods = mods;

	public IReadOnlyList<InstalledMod> Mods => mods;

	public int EnabledCount => mods.Count(mod => mod.Enabled);

	public static ModCatalogue Of(IEnumerable<InstalledMod> mods) =>
		new([.. mods.OrderBy(mod => mod.SortKey, StringComparer.Ordinal).ThenBy(mod => !mod.Enabled)]);

	/// <summary>
	/// Идентификаторы, которые встречаются среди включённых модов больше одного раза.
	/// Отключённая копия дубликатом не считается.
	/// </summary>
	public IReadOnlyList<ModId> DuplicateIds =>
	[
		.. mods
			.Where(mod => mod is { Enabled: true, Id.IsEmpty: false })
			.GroupBy(mod => mod.Id)
			.Where(group => group.Count() > 1)
			.Select(group => group.Key)
			.OrderBy(id => id.Value, StringComparer.Ordinal),
	];

	/// <summary>
	/// Зависимости мода, которых нет среди включённых и которые не встроены в игру.
	/// </summary>
	/// <remarks>
	/// Считается по включённым: если зависимость лежит в <c>.disabled</c>, мод при запуске
	/// сломается так же, как без неё.
	/// </remarks>
	public IReadOnlyList<string> MissingDependenciesOf(InstalledMod mod)
	{
		var present = mods
			.Where(item => item is { Enabled: true, Id.IsEmpty: false })
			.Select(item => item.Id.Value)
			.Concat(builtIn)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		return
		[
			.. mod.Dependencies.Keys
				.Where(required => !present.Contains(required))
				.OrderBy(required => required, StringComparer.OrdinalIgnoreCase),
		];
	}

	/// <summary>Найти мод по имени файла в любом из двух каталогов.</summary>
	public Result<InstalledMod> Find(ModArchiveName name) =>
		mods.FirstOrDefault(mod => mod.File == name) is { } found
			? found
			: Failure.NotFound("Мод не найден");
}

/// <summary>
/// Имя сохранённого набора модов. Набор символов шире, чем у имени файла: профиль — ключ в
/// <c>.profiles.json</c>, и в нём допустимы кириллица и пробелы.
/// </summary>
public readonly partial record struct ModProfileName
{
	public const int MaxLength = 48;

	private readonly string? value;

	private ModProfileName(string value) => this.value = value;

	public string Value => value ?? throw new InvalidOperationException("Имя профиля не задано");

	public bool IsEmpty => value is null;

	[GeneratedRegex(@"^[A-Za-z0-9А-Яа-яЁё _.-]+$")]
	private static partial Regex Pattern { get; }

	public static Result<ModProfileName> Create(string? raw)
	{
		var value = (raw ?? string.Empty).Trim();

		if (value.Length == 0 || value.Length > MaxLength)
		{
			return Failure.Invalid("Имя профиля модов пусто или слишком длинное");
		}

		return Pattern.IsMatch(value)
			? new ModProfileName(value)
			: Failure.Invalid("Недопустимое имя профиля модов");
	}

	public static ModProfileName Parse(string raw) => Names.OrThrow(Create(raw), nameof(raw));

	public override string ToString() => value ?? string.Empty;
}

/// <summary>
/// Сохранённый набор включённых модов.
/// </summary>
/// <remarks>
/// Профиль хранит имена файлов, а не идентификаторы, и применяется перемещением файлов между
/// <c>Mods</c> и <c>Mods/.disabled</c>. Если какого-то файла больше нет, профиль не
/// применяется совсем: частично применённый набор не совпадает ни с прежним, ни с
/// сохранённым.
/// </remarks>
public sealed record ModProfile(ModProfileName Name, IReadOnlyList<ModArchiveName> Files)
{
	/// <summary>Можно ли применить профиль к установленным модам.</summary>
	public Result<Done> CanApplyTo(ModCatalogue catalogue)
	{
		var installed = catalogue.Mods.Select(mod => mod.File).ToHashSet();
		var absent = Files.Where(file => !installed.Contains(file)).ToArray();

		return absent.Length == 0
			? Done.Ok
			: Failure.Conflict(
				"Профиль ссылается на моды, которых больше нет: " + string.Join(", ", absent.Select(file => file.Value)));
	}
}
