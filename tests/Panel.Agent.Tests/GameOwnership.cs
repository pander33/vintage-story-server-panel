namespace Panel.Agent.Tests;

/// <summary>
/// Может ли тест отдать файл игре: <see cref="Content.GameFiles.ChangeOwnerAsync"/> на Linux
/// вызывает <c>chown vsgame:vsgame</c>, для которого нужны root и сама учётная запись. На
/// Windows смена владельца ничего не делает.
/// </summary>
internal static class GameOwnership
{
	public static bool Possible =>
		OperatingSystem.IsWindows()
		|| (Environment.IsPrivilegedProcess
			&& File.Exists("/etc/passwd")
			&& File.ReadLines("/etc/passwd").Any(line => line.StartsWith(Content.GameFiles.GameUser + ":", StringComparison.Ordinal)));
}
