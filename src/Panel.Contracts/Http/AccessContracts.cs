using System.Text.Json.Serialization;

namespace Panel.Contracts.Http;

/// <summary>Ответ, у которого нет содержания: <c>{"ok": true}</c>.</summary>
public sealed record OkResponse
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;

	public static OkResponse Instance { get; } = new();
}

/// <summary>
/// <c>GET /api/session</c>. Отвечает и без входа: так фронтенд узнаёт, что нужно показать
/// форму входа.
/// </summary>
public sealed record SessionResponse
{
	[JsonPropertyName("authenticated")] public required bool Authenticated { get; init; }
	[JsonPropertyName("csrf")] public string? Csrf { get; init; }
	[JsonPropertyName("user")] public string? User { get; init; }
	[JsonPropertyName("role")] public string? Role { get; init; }
}

public sealed record LoginRequest
{
	[JsonPropertyName("username")] public string Username { get; init; } = string.Empty;
	[JsonPropertyName("password")] public string Password { get; init; } = string.Empty;
	[JsonPropertyName("otp")] public string Otp { get; init; } = string.Empty;
}

/// <summary>
/// Ответ на вход: поля учётной записи лежат в корне рядом с <c>ok</c> и <c>csrf</c>.
/// </summary>
public sealed record LoginResponse
{
	[JsonPropertyName("ok")] public bool Ok { get; init; } = true;
	[JsonPropertyName("csrf")] public required string Csrf { get; init; }
	[JsonPropertyName("user")] public required string User { get; init; }
	[JsonPropertyName("role")] public required string Role { get; init; }
	[JsonPropertyName("generation")] public required int Generation { get; init; }
}

public sealed record AccountsResponse
{
	[JsonPropertyName("users")] public required IReadOnlyList<AccountSummary> Users { get; init; }
}

public sealed record AccountSummary
{
	[JsonPropertyName("username")] public required string Username { get; init; }
	[JsonPropertyName("role")] public required string Role { get; init; }
	[JsonPropertyName("totp_enabled")] public required bool TotpEnabled { get; init; }
}

public sealed record AccountRequest
{
	[JsonPropertyName("username")] public string Username { get; init; } = string.Empty;
	[JsonPropertyName("password")] public string Password { get; init; } = string.Empty;
	[JsonPropertyName("role")] public string Role { get; init; } = "viewer";
}

public sealed record TotpRequest
{
	[JsonPropertyName("password")] public string Password { get; init; } = string.Empty;
	[JsonPropertyName("code")] public string Code { get; init; } = string.Empty;
}

/// <summary>
/// Секрет второго фактора и ссылка otpauth для QR-кода. Секрет отдаётся клиенту один раз, при
/// настройке; позже его получить нельзя.
/// </summary>
public sealed record TotpBeginResponse
{
	[JsonPropertyName("secret")] public required string Secret { get; init; }
	[JsonPropertyName("uri")] public required string Uri { get; init; }
}
