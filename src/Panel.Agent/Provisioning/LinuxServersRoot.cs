using System.Globalization;
using Panel.SharedKernel;

namespace Panel.Agent.Provisioning;

/// <summary>
/// Проверка предка каталога серверов на Linux по выводу <c>stat -L -c '%u %a'</c>.
/// </summary>
/// <remarks>
/// Каталог серверов принадлежит root, внутри лежат деревья игрового пользователя, которые
/// запускает systemd. Если в предок может писать кто-то кроме root, каталог серверов можно
/// переименовать и положить на его место свой, и юнит запустит чужую программу. Бит sticky
/// (<c>/tmp</c>) не даёт переименовывать чужое.
/// </remarks>
public static class LinuxServersRoot
{
	private const int groupOrOtherWrite = 0b000_010_010; // 0o022

	private const int sticky = 0b001_000_000_000; // 0o1000

	/// <summary>Разобрать <c>uid режим</c>; режим восьмеричный, как его печатает <c>%a</c>.</summary>
	public static (int Uid, int Mode)? Parse(string output)
	{
		var parts = (output ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length != 2
			|| !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var uid)
			|| parts[1].Length is 0 or > 4
			|| parts[1].Any(digit => digit is < '0' or > '7'))
		{
			return null;
		}

		return (uid, Convert.ToInt32(parts[1], 8));
	}

	/// <summary>Чем опасен предок; <see langword="null"/>, если ничем.</summary>
	public static Phrase? Weakness(string path, int uid, int mode)
	{
		if (uid != 0)
		{
			return Phrase.Format($"Каталог {path} принадлежит не root: его владелец может подменить каталог серверов. Выберите каталог внутри каталога root, например /srv/vs-panel");
		}

		if ((mode & groupOrOtherWrite) != 0 && (mode & sticky) == 0)
		{
			return Phrase.Format($"В каталог {path} может писать не только root: каталог серверов в нём можно подменить. Выберите каталог внутри каталога root, например /srv/vs-panel");
		}

		return null;
	}
}
