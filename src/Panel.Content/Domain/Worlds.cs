using Panel.SharedKernel;

namespace Panel.Content.Domain;

/// <summary>
/// Куда перемещается мир, убранный из списка: <c>.archive</c> — хранить долго, <c>.trash</c> —
/// убрать с глаз. Файл в обоих случаях не удаляется.
/// </summary>
public enum WorldDisposition
{
	/// <summary>Каталог <c>Saves/.archive</c>.</summary>
	Archived,

	/// <summary>Каталог <c>Saves/.trash</c>.</summary>
	Trashed,
}

/// <summary>Преобразование в строку реестра и обратно: <c>archive</c> / <c>trash</c>.</summary>
public static class WorldDispositionWire
{
	public static string ToWire(WorldDisposition value) =>
		value == WorldDisposition.Archived ? "archive" : "trash";

	public static Result<WorldDisposition> FromWire(string? raw) => (raw ?? string.Empty).Trim() switch
	{
		"archive" => WorldDisposition.Archived,
		"trash" => WorldDisposition.Trashed,
		_ => Failure.Invalid("Недопустимый режим архивации мира"),
	};

	/// <summary>
	/// Фраза подтверждения зависит от назначения: для архива и корзины это разные слова, хотя
	/// на диске это одно и то же перемещение.
	/// </summary>
	public static Confirmation ConfirmationFor(WorldDisposition value, WorldFileName world) =>
		value == WorldDisposition.Archived
			? Confirmation.ToArchive(world.Value)
			: Confirmation.ToDelete(world.Value);
}

/// <summary>
/// Стиль игры нового мира: значение <c>WorldConfig.PlayStyle</c> и ключ его подписи
/// <c>WorldConfig.PlayStyleLangCode</c>.
/// </summary>
/// <remarks>
/// <para>
/// Набор стилей не закрыт: его объявляют игра и моды (<c>playstyles</c> в схеме параметров мира).
/// Панель проверяет только форму кода; есть ли такой стиль на сервере, решает демон по схеме
/// этого сервера — он же берёт из схемы ключ подписи. У игры 1.22.7 он не всегда
/// <c>"preset-" + код</c>: у творческого стиля это <c>creativebuilding</c>.
/// </para>
/// <para>
/// Если схему прочитать не из чего (нет сборок игры), демон принимает только
/// <see cref="Builtin"/> с прежним правилом <c>"preset-" + код</c>, как 1.2.x.
/// </para>
/// </remarks>
public sealed partial record PlayStyle
{
	private PlayStyle(string code, string languageCode)
	{
		Code = code;
		LanguageCode = languageCode;
	}

	/// <summary>Стили игры 1.22 — запасной набор, когда схемы нет.</summary>
	public static IReadOnlyList<string> Builtin { get; } =
		["surviveandbuild", "exploration", "wildernesssurvival", "homosapiens", "creativebuilding"];

	public string Code { get; }

	public string LanguageCode { get; }

	/// <summary>Код из запроса: только форма, ключ подписи — по прежнему правилу.</summary>
	public static Result<PlayStyle> Create(string? raw)
	{
		var value = (raw ?? string.Empty).Trim();
		return Shape().IsMatch(value)
			? new PlayStyle(value, "preset-" + value)
			: Failure.Invalid("Недопустимый стиль игры");
	}

	/// <summary>Стиль, объявленный схемой сервера, с её ключом подписи.</summary>
	public static Result<PlayStyle> CreateDeclared(string? code, string? languageCode)
	{
		var style = Create(code);
		if (!style.IsSuccess)
		{
			return style;
		}

		var language = (languageCode ?? string.Empty).Trim();
		return Shape().IsMatch(language)
			? new PlayStyle(style.Value.Code, language)
			: style;
	}

	[System.Text.RegularExpressions.GeneratedRegex(@"\A[A-Za-z0-9._-]{1,64}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
	private static partial System.Text.RegularExpressions.Regex Shape();
}

/// <summary>Мир на диске.</summary>
public sealed record WorldFile(WorldFileName Name, long Size, DateTimeOffset Modified, bool Active);

/// <summary>Мир, убранный в <c>.archive</c> или <c>.trash</c>.</summary>
public sealed record StoredWorld(
	StoredWorldName StoredName,
	WorldDisposition Disposition,
	long Size,
	DateTimeOffset Modified)
{
	/// <summary>Имя, под которым мир был до перемещения.</summary>
	public string OriginalName => StoredName.OriginalName;
}

/// <summary>
/// Набор миров сервера.
/// </summary>
/// <remarks>
/// Активный мир нельзя архивировать или удалить, а менять состав миров можно только при
/// остановленной службе. Оба правила относятся к набору, а не к отдельному файлу.
/// </remarks>
public sealed class WorldCollection
{
	private readonly List<WorldFile> worlds;

	private WorldCollection(List<WorldFile> worlds, bool serverRunning)
	{
		this.worlds = worlds;
		ServerRunning = serverRunning;
	}

	public IReadOnlyList<WorldFile> Worlds => worlds;

	public bool ServerRunning { get; }

	/// <summary>Активный мир: тот, на который указывает <c>WorldConfig.SaveFileLocation</c>.</summary>
	public WorldFile? Active => worlds.FirstOrDefault(world => world.Active);

	public static WorldCollection Of(IEnumerable<WorldFile> worlds, bool serverRunning) =>
		new([.. worlds.OrderByDescending(world => world.Modified)], serverRunning);

	/// <summary>
	/// Можно ли сейчас менять состав миров. Работающий сервер держит <c>.vcdbs</c> открытым, и
	/// подмена файла повредит базу.
	/// </summary>
	public Result<Done> CanModify() =>
		ServerRunning
			? Failure.Conflict("Остановите сервер Vintage Story перед изменением миров")
			: Done.Ok;

	public Result<WorldFile> Find(WorldFileName name) =>
		worlds.FirstOrDefault(world => world.Name == name) is { } found
			? found
			: Failure.NotFound("Мир не найден");

	/// <summary>Проверка перед архивацией: служба остановлена и мир не активен.</summary>
	public Result<WorldFile> CanRemove(WorldFileName name) =>
		CanModify()
			.Bind(_ => Find(name))
			.Bind(world => world.Active
				? Result.Fail<WorldFile>(Failure.Conflict("Выберите другой активный мир, прежде чем убирать этот"))
				: world);

	/// <summary>Проверка перед созданием или импортом мира: имя должно быть свободно.</summary>
	public Result<Done> CanAdd(WorldFileName name) =>
		CanModify()
			.Bind(_ => worlds.Any(world => world.Name == name)
				? Result.Fail<Done>(Failure.Conflict("Мир с таким именем файла уже существует"))
				: Done.Ok);
}

/// <summary>
/// Признак файла мира: база SQLite. Проверяется при импорте и при восстановлении из копии.
/// </summary>
public static class WorldDatabase
{
	/// <summary>Заголовок файла базы: «SQLite format 3» и завершающий ноль.</summary>
	public static ReadOnlySpan<byte> Signature => "SQLite format 3\u0000"u8;

	public static int SignatureLength => Signature.Length;

	public static Result<Done> CheckSignature(ReadOnlySpan<byte> head) =>
		head.Length >= Signature.Length && head[..Signature.Length].SequenceEqual(Signature)
			? Done.Ok
			: Failure.Invalid("Файл не является базой мира SQLite");
}
