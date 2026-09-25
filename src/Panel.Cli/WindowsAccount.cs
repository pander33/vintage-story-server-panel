using System.Runtime.Versioning;
using System.Security.Principal;

namespace Panel.Cli;

/// <summary>
/// Права того, кто запустил <c>vs-panel</c> на Windows.
/// </summary>
/// <remarks>
/// Службы работают от LocalSystem (§69), поэтому учётная запись пользователя установщику не
/// нужна: ни пароль, ни право «Вход в качестве службы». Нужно только, чтобы команды, меняющие
/// службы и файлы, запускались из командной строки администратора.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsAccount
{
	public static bool IsAdministrator()
	{
		using var identity = WindowsIdentity.GetCurrent();
		return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
	}
}
