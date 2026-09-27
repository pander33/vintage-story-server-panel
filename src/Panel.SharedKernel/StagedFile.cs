namespace Panel.SharedKernel;

/// <summary>
/// Файл, который веб-процесс положил в каталог промежуточного хранения, чтобы
/// привилегированная сторона забрала его по имени.
/// </summary>
/// <remarks>
/// <para>
/// Мод весит десятки мегабайт, мир — гигабайты, через протокол демона они не передаются:
/// веб-процесс пишет файл в <c>staging</c>, а демону уходит только имя. Демон проверяет, что
/// файл лежит именно в этом каталоге.
/// </para>
/// <para>
/// Тип содержит имя, а не путь: каталог определяет демон, как и пути к игровым каталогам.
/// </para>
/// <para>
/// Тип находится в разделяемом ядре, потому что staging используют два контекста: Content
/// (моды, миры, настройки модов) и Configuration (serverconfig.json).
/// </para>
/// </remarks>
public readonly record struct StagedFile
{
	private readonly string? name;

	private StagedFile(string name) => this.name = name;

	public string Name => name ?? throw new InvalidOperationException("Имя временного файла не задано");

	public bool IsEmpty => name is null;

	/// <summary>
	/// Приставки, по которым демон различает назначение файла. Мир, попавший в staging с
	/// приставкой <c>mod-…</c>, распакован не будет: приставка входит в проверку.
	/// </summary>
	public static class Purpose
	{
		public const string Mod = "mod-";
		public const string World = "world-";
		public const string Config = "config-";

		/// <summary>Пакет сервера <c>.tar.gz</c> для обновления игры.</summary>
		public const string Game = "game-";
		public const string Transfer = "transfer-";
	}

	public static Result<StagedFile> Create(string? raw)
	{
		var value = (raw ?? string.Empty).Trim();

		if (!PlainFileName.IsPlain(value) || value.Length is 0 or > 96)
		{
			return Failure.Invalid("Недопустимое имя временного файла");
		}

		var known = value.StartsWith(Purpose.Mod, StringComparison.Ordinal)
			|| value.StartsWith(Purpose.World, StringComparison.Ordinal)
			|| value.StartsWith(Purpose.Config, StringComparison.Ordinal)
			|| value.StartsWith(Purpose.Game, StringComparison.Ordinal)
			|| value.StartsWith(Purpose.Transfer, StringComparison.Ordinal);

		return known
			? new StagedFile(value)
			: Failure.Invalid("Временный файл создан не панелью");
	}

	/// <summary>Новое имя со случайной частью, чтобы имена не совпадали.</summary>
	public static StagedFile CreateFresh(string purpose, string extension, string token) =>
		new(purpose + token + extension);

	public override string ToString() => name ?? string.Empty;
}
