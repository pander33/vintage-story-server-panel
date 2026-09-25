using Panel.SharedKernel;

namespace Panel.Access.Sessions;

/// <summary>
/// Ключ подписи сессий из <c>secret.key</c>, не короче 32 байт. install.sh сохраняет файл
/// при обновлении, поэтому после перехода на .NET сессии остаются действительными (ADR-06).
/// </summary>
public sealed class SessionSecret
{
	private readonly byte[] key;

	private SessionSecret(byte[] key) => this.key = key;

	public ReadOnlySpan<byte> Key => key;

	public static Result<SessionSecret> Create(byte[] key)
	{
		ArgumentNullException.ThrowIfNull(key);
		return key.Length >= 32
			? new SessionSecret(key)
			: Failure.Invalid($"Слабый ключ сессий: {key.Length} байт, требуется не менее 32");
	}

	public static Result<SessionSecret> Load(string path)
	{
		try
		{
			return Create(File.ReadAllBytes(path));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return Failure.Unavailable($"Не читается ключ сессий {path}: {exception.Message}");
		}
	}
}
