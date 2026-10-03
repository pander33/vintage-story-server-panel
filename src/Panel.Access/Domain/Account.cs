using System.Text.Json.Nodes;
using Panel.Access.Passwords;

namespace Panel.Access.Domain;

/// <summary>Что предъявляет входящий: пароль и, если включён, одноразовый код.</summary>
public sealed record Credentials(string Password, OneTimeCode OneTimeCode = default);

/// <summary>
/// Учётная запись панели. Хранит исходный JSON-узел, чтобы при записи не терялись поля,
/// о которых эта версия не знает.
/// </summary>
public sealed record Account
{
	public required UserName Name { get; init; }

	public required PasswordHash Password { get; init; }

	public required Role Role { get; init; }

	/// <summary>
	/// Поколение. Растёт при смене пароля или роли и этим отзывает все выданные сессии;
	/// другого механизма отзыва нет.
	/// </summary>
	public int Generation { get; init; }

	public string TotpSecret { get; init; } = string.Empty;

	public bool TotpEnabled { get; init; }

	/// <summary>
	/// Секрет, показанный пользователю и ещё не подтверждённый кодом. Хранится отдельно,
	/// чтобы неудачная настройка второго фактора не выключила действующий.
	/// </summary>
	public string PendingTotpSecret { get; init; } = string.Empty;

	public JsonObject? Raw { get; init; }

	/// <summary>
	/// Проверка пароля, затем второго фактора, как в <c>authenticate()</c>. Причина отказа
	/// не сообщается: по ней было бы видно, что пароль подошёл.
	/// </summary>
	public bool Accepts(Credentials credentials, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(credentials);

		if (!Password.Verify(credentials.Password))
		{
			return false;
		}

		return !TotpEnabled || Totp.Verify(TotpSecret, credentials.OneTimeCode.Value, now);
	}
}
