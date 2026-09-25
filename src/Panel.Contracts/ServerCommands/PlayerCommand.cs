using System.Text.RegularExpressions;
using Panel.SharedKernel;

namespace Panel.Contracts.ServerCommands;

/// <summary>
/// Действие над игроком: белый список, бан, отключение, права, режим игры, а также
/// объявление всем.
/// </summary>
/// <remarks>
/// <para>
/// Строка консоли собирается здесь из проверенных частей: имя игрока только из латиницы,
/// цифр, <c>_</c> и <c>-</c>; причина и текст объявления без управляющих символов. Перевод
/// строки в тексте объявления стал бы второй командой.
/// </para>
/// <para>
/// Лишение прав (<c>deop</c>) — не команда игры: у выделенного сервера нет <c>/deop</c>.
/// Это назначение роли по умолчанию из <c>serverconfig.json</c>, а роль знает только демон,
/// поэтому <see cref="Render"/> принимает её параметром.
/// </para>
/// </remarks>
public sealed partial record PlayerCommand
{
	private const int maxAnnouncement = 300;
	private const int maxReason = 160;

	public static IReadOnlyList<string> Actions { get; } =
		["whitelist-add", "whitelist-remove", "ban", "unban", "kick", "op", "deop", "announce", "gamemode"];

	private PlayerCommand(string action, string player, string detail)
	{
		Action = action;
		Player = player;
		Detail = detail;
	}

	public string Action { get; }

	/// <summary>Пусто для объявления.</summary>
	public string Player { get; }

	/// <summary>Причина, режим игры или текст объявления, в зависимости от действия.</summary>
	public string Detail { get; }

	public bool IsAnnouncement => Action == "announce";

	/// <summary>
	/// Полная проверка; тексты отказов показываются в интерфейсе.
	/// </summary>
	public static Result<PlayerCommand> Create(string? action, string? player, string? detail)
	{
		if (action is null || !Actions.Contains(action, StringComparer.Ordinal))
		{
			return Failure.Invalid("Недопустимая команда");
		}

		if (action == "gamemode" && !ServerCommandCatalogue.GameModes.Contains(detail ?? string.Empty, StringComparer.Ordinal))
		{
			return Failure.Invalid("Недопустимый игровой режим");
		}

		if (action == "announce")
		{
			var message = SafeText(detail, maxAnnouncement, allowEmpty: false);
			return message is null
				? Failure.Invalid("Недопустимый текст объявления")
				: new PlayerCommand(action, string.Empty, message);
		}

		if (player is null || !PlayerName().IsMatch(player))
		{
			return Failure.Invalid("Имя игрока может содержать только A-Z, a-z, 0-9, _ и -");
		}

		var reason = SafeText(detail, maxReason, allowEmpty: true);
		return reason is null
			? Failure.Invalid("Недопустимая причина")
			: new PlayerCommand(action, player, reason);
	}

	/// <summary>Строка консоли. <paramref name="defaultRole"/> нужна только для <c>deop</c>.</summary>
	public string Render(string defaultRole) => Action switch
	{
		"announce" => $"/announce {Detail}",
		"gamemode" => $"/gamemode {Player} {Detail}",
		"deop" => $"/player {Player} role {defaultRole}",
		"whitelist-add" => $"/player {Player} whitelist on",
		"whitelist-remove" => $"/player {Player} whitelist off",
		"ban" => Detail.Length > 0 ? $"/ban {Player} {Detail}" : $"/ban {Player}",
		"unban" => $"/unban {Player}",
		"kick" => Detail.Length > 0 ? $"/kick {Player} {Detail}" : $"/kick {Player}",
		"op" => $"/op {Player}",
		_ => throw new InvalidOperationException($"Действие {Action} не проверено"),
	};

	/// <summary>Можно ли подставить роль по умолчанию в команду.</summary>
	public static bool IsValidRole(string? role) => role is not null && RoleCode().IsMatch(role);

	public IReadOnlyList<string> ToArguments() => [Action, Player, Detail];

	/// <summary>Обрезка пробелов, проверка длины и отсутствия символов с кодом меньше пробела.</summary>
	private static string? SafeText(string? raw, int maximum, bool allowEmpty)
	{
		var value = (raw ?? string.Empty).Trim();

		return (value.Length == 0 && !allowEmpty)
			   || value.EnumerateRunes().Count() > maximum
			   || value.Any(symbol => symbol < ' ')
			? null
			: value;
	}

	[GeneratedRegex(@"^[A-Za-z0-9_-]{1,32}\z", RegexOptions.CultureInvariant)]
	private static partial Regex PlayerName();

	[GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)]
	private static partial Regex RoleCode();
}
