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

/// <summary>Стиль игры нового мира; набор закрыт и совпадает с <c>PLAYSTYLES</c>.</summary>
public enum PlayStyle
{
	SurviveAndBuild,
	Exploration,
	WildernessSurvival,
	HomoSapiens,
	CreativeBuilding,
}

public static class PlayStyleWire
{
	private static readonly (PlayStyle Style, string Wire)[] map =
	[
		(PlayStyle.SurviveAndBuild, "surviveandbuild"),
		(PlayStyle.Exploration, "exploration"),
		(PlayStyle.WildernessSurvival, "wildernesssurvival"),
		(PlayStyle.HomoSapiens, "homosapiens"),
		(PlayStyle.CreativeBuilding, "creativebuilding"),
	];

	public static string ToWire(PlayStyle value) => map.First(item => item.Style == value).Wire;

	/// <summary>
	/// Код языка, который игра ожидает рядом со стилем: <c>"preset-" + стиль</c>.
	/// </summary>
	public static string LanguageCode(PlayStyle value) => "preset-" + ToWire(value);

	public static Result<PlayStyle> FromWire(string? raw)
	{
		var value = (raw ?? string.Empty).Trim();

		return map.FirstOrDefault(item => item.Wire == value) is { Wire.Length: > 0 } found
			? found.Style
			: Failure.Invalid("Недопустимый стиль игры");
	}

	public static IReadOnlyList<string> All => [.. map.Select(item => item.Wire)];
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
