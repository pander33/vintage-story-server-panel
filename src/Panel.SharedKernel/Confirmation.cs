namespace Panel.SharedKernel;

/// <summary>
/// Фраза подтверждения для необратимого или дорогого действия.
/// </summary>
/// <remarks>
/// <para>
/// Фраза состоит из глагола и имени: «ВОССТАНОВИТЬ имя», «УДАЛИТЬ имя» и т. д. Исключение —
/// удаление мода, где нужно только имя; фразу набирает человек по подсказке фронтенда
/// (ADR-16), поэтому она не меняется.
/// </para>
/// <para>
/// С этим типом новое разрушительное действие получает подтверждение через сигнатуру метода.
/// </para>
/// <para>
/// Тип находится в разделяемом ядре: фраза нужна и для удаления сервера из реестра (Fleet), и
/// для очистки истории, то есть не одному контексту. Кроме того, её содержимое — текст,
/// набранный человеком, и голая строка на входе здесь не означает пропущенную проверку.
/// </para>
/// </remarks>
public readonly record struct Confirmation
{
	private readonly string expected;

	private Confirmation(string expected) => this.expected = expected;

	/// <summary>Фраза, которую нужно набрать; показывается в интерфейсе как есть.</summary>
	public string Expected => expected ?? string.Empty;

	/// <summary>«УДАЛИТЬ имя»: для резервной копии и для перемещения мира в корзину.</summary>
	public static Confirmation ToDelete(string subject) => new("УДАЛИТЬ " + subject);

	/// <summary>«АРХИВИРОВАТЬ имя»: мир перемещается в <c>.archive</c>, а не в корзину.</summary>
	public static Confirmation ToArchive(string subject) => new("АРХИВИРОВАТЬ " + subject);

	/// <summary>«ВОССТАНОВИТЬ имя»: развернуть резервную копию поверх данных.</summary>
	public static Confirmation ToRestore(string subject) => new("ВОССТАНОВИТЬ " + subject);

	/// <summary>«ИЗВЛЕЧЬ имя»: достать мир из резервной копии под новым именем.</summary>
	public static Confirmation ToExtract(string subject) => new("ИЗВЛЕЧЬ " + subject);

	/// <summary>
	/// Только имя, без глагола: так подтверждается удаление мода. Отдельная фабрика, чтобы это
	/// исключение было видно в месте вызова.
	/// </summary>
	public static Confirmation ByNameAlone(string subject) => new(subject);

	/// <summary>
	/// Фраза без предмета, например «ОЧИСТИТЬ СТАРЫЕ КОПИИ»: для действий над набором, где в
	/// подтверждении нечего назвать.
	/// </summary>
	public static Confirmation Exactly(string phrase) => new(phrase);

	/// <summary>
	/// Проверка набранного. Сравнение обычное, не за постоянное время: фраза не секрет, она
	/// показывается на экране.
	/// </summary>
	public Result<Done> Check(string? typed) =>
		string.Equals((typed ?? string.Empty).Trim(), Expected.Trim(), StringComparison.Ordinal)
			? Done.Ok
			: Failure.Invalid("Фраза подтверждения не совпадает");

	public override string ToString() => Expected;
}
