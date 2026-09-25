namespace Panel.SharedKernel;

/// <summary>
/// Только имя файла: без разделителей пути в любом виде, без управляющих символов, не
/// <c>.</c> и не <c>..</c>.
/// </summary>
/// <remarks>
/// Обратная косая черта запрещена наравне с прямой, хотя на Linux это обычный символ имени:
/// реестр поддерживает <c>layout: windows</c>, и безопасное на одной платформе имя не должно
/// стать путём на другой.
/// </remarks>
public static class PlainFileName
{
	public static bool IsPlain(string value) =>
		value.Length > 0
		&& value is not ("." or "..")
		&& !value.Contains('/', StringComparison.Ordinal)
		&& !value.Contains('\\', StringComparison.Ordinal)
		&& !value.Contains(':', StringComparison.Ordinal)
		&& !value.Any(char.IsControl);
}
