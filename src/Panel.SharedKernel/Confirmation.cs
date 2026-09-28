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
/// Интерфейс двуязычный, и человек набирает фразу на языке, который видит: у каждой фразы есть
/// русская и английская форма, и подходит любая из двух. Смысл фразы — набрать её осознанно, а
/// не угадать: она не секрет, и вторая форма её не ослабляет.
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
	private readonly string english;

	private Confirmation(string expected, string english)
	{
		this.expected = expected;
		this.english = english;
	}

	/// <summary>Русская форма фразы.</summary>
	public string Expected => expected ?? string.Empty;

	/// <summary>Английская форма фразы.</summary>
	public string English => english ?? string.Empty;

	/// <summary>«УДАЛИТЬ имя»: для резервной копии и для перемещения мира в корзину.</summary>
	public static Confirmation ToDelete(string subject) => new("УДАЛИТЬ " + subject, "DELETE " + subject);

	/// <summary>«АРХИВИРОВАТЬ имя»: мир перемещается в <c>.archive</c>, а не в корзину.</summary>
	public static Confirmation ToArchive(string subject) => new("АРХИВИРОВАТЬ " + subject, "ARCHIVE " + subject);

	/// <summary>«ВОССТАНОВИТЬ имя»: развернуть резервную копию поверх данных.</summary>
	public static Confirmation ToRestore(string subject) => new("ВОССТАНОВИТЬ " + subject, "RESTORE " + subject);

	/// <summary>«ИЗВЛЕЧЬ имя»: достать мир из резервной копии под новым именем.</summary>
	public static Confirmation ToExtract(string subject) => new("ИЗВЛЕЧЬ " + subject, "EXTRACT " + subject);

	/// <summary>
	/// Только имя, без глагола: так подтверждается удаление мода. Отдельная фабрика, чтобы это
	/// исключение было видно в месте вызова.
	/// </summary>
	public static Confirmation ByNameAlone(string subject) => new(subject, subject);

	/// <summary>
	/// Фраза, заданная целиком на обоих языках, например «ОЧИСТИТЬ СТАРЫЕ КОПИИ» и
	/// «CLEAR OLD BACKUPS»: для действий над набором и для глаголов, нужных одному маршруту.
	/// </summary>
	public static Confirmation Exactly(string russian, string english) => new(russian, english);

	/// <summary>
	/// Проверка набранного: подходит любая из двух форм. Сравнение обычное, не за постоянное
	/// время: фраза не секрет, она показывается на экране.
	/// </summary>
	public Result<Done> Check(string? typed)
	{
		var text = (typed ?? string.Empty).Trim();
		return string.Equals(text, Expected.Trim(), StringComparison.Ordinal) || string.Equals(text, English.Trim(), StringComparison.Ordinal)
			? Done.Ok
			: Failure.Invalid("Фраза подтверждения не совпадает");
	}

	public override string ToString() => Expected;
}
