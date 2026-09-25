using System.Security.Cryptography;
using System.Text;
using Panel.Access.Domain;
using Panel.SharedKernel;

namespace Panel.Access.Authorization;

/// <summary>
/// Проверка CSRF-токена: токен хранится в сессии и должен прийти в заголовке запроса.
/// </summary>
/// <remarks>
/// Встроенная защита ASP.NET Core выключена (ADR-06): она рассчитана на cookie и скрытое
/// поле формы, а фронтенд панели отправляет только fetch с заголовком. Роль проверяет
/// отдельно <see cref="CommandAuthorizer"/>.
/// </remarks>
public static class CsrfGuard
{
	public const string HeaderName = "X-CSRF-Token";

	public static Result<Done> Check(Identity identity, string? presented)
	{
		ArgumentNullException.ThrowIfNull(identity);

		if (string.IsNullOrEmpty(presented) || identity.CsrfToken.Length == 0)
		{
			return Failure.Forbidden("Некорректный CSRF-токен");
		}

		var same = CryptographicOperations.FixedTimeEquals(
			Encoding.UTF8.GetBytes(presented),
			Encoding.UTF8.GetBytes(identity.CsrfToken));

		return same ? Done.Ok : Failure.Forbidden("Некорректный CSRF-токен");
	}
}
