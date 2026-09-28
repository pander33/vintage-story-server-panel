using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Provisioning;

/// <summary>
/// Каталог серверов на Windows вне <c>ProgramData\vs-panel</c> с тем же списком доступа, что
/// установщик ставит каталогу данных: SYSTEM и администраторы.
/// </summary>
/// <remarks>
/// <para>
/// Игра работает от LocalSystem и запускает файлы из этого дерева. Если обычный пользователь
/// может писать в каталог, любая программа в его сеансе получает путь к SYSTEM (findings §66).
/// На несистемных дисках так по умолчанию: новый каталог в корне <c>D:\</c> наследует право
/// «Изменение» для всех прошедших проверку.
/// </para>
/// <para>
/// Своего списка доступа недостаточно: каталог, который пользователь может переименовать
/// вместе с родителем, можно и подменить. Поэтому проверяются и предки: владелец и права
/// «удаление», «удаление подпапок и файлов», «смена разрешений», «смена владельца» у всех,
/// кроме SYSTEM, администраторов и TrustedInstaller. Промежуточные каталоги демон не создаёт,
/// потому что они унаследовали бы права диска.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WindowsServersRoot
{
	/// <summary>Права, опасные у любого предка: удаление содержимого, смена разрешений и владельца, GENERIC_ALL.</summary>
	private const int dangerousRights =
		(int) (FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)
		| 0x10000000;

	private static readonly SecurityIdentifier system = new(WellKnownSidType.LocalSystemSid, null);

	private static readonly SecurityIdentifier administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

	private static readonly SecurityIdentifier trustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

	/// <summary>
	/// Проверить предков и подготовить каталог.
	/// </summary>
	/// <param name="owned">
	/// Каталог уже принадлежит панели (текущий или прежний каталог серверов). В нём могут быть
	/// серверы, и его список доступа не переписывается: это долго на большом дереве.
	/// </param>
	public static Result<Done> Secure(ServersRoot root, bool owned)
	{
		ArgumentNullException.ThrowIfNull(root);

		var drive = new DriveInfo(root.Path[..1]);
		if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
		{
			return Failure.Invalid($"Диск {drive.Name} — не локальный жёсткий диск: серверы создаются только на нём");
		}

		if (drive.DriveFormat is not ("NTFS" or "ReFS"))
		{
			return Failure.Invalid($"Файловая система диска {drive.Name} — {drive.DriveFormat}: без списков доступа NTFS каталог серверов не защитить");
		}

		if (!Directory.Exists(root.Parent))
		{
			return Failure.Invalid($"Каталога {root.Parent} нет: каталог серверов создаётся только внутри существующего");
		}

		for (var ancestor = new DirectoryInfo(root.Parent); ancestor is not null; ancestor = ancestor.Parent)
		{
			if (Weakness(ancestor.FullName, ancestor.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), volumeRoot: ancestor.Parent is null) is { } weakness)
			{
				return Failure.Invalid(weakness);
			}
		}

		var directory = new DirectoryInfo(root.Path);
		if (directory.Exists)
		{
			if (owned || IsProtected(directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)))
			{
				return Done.Ok;
			}

			if (directory.EnumerateFileSystemInfos().Any())
			{
				return Failure.Conflict($"Каталог {root.Path} не пуст: выберите пустой или новый каталог");
			}
		}

		directory.Create();
		directory.SetAccessControl(Protected());
		return Done.Ok;
	}

	/// <summary>
	/// Чем опасен предок: владелец или право, позволяющее обычному пользователю переименовать
	/// или удалить содержимое. Правила «только для наследования» не учитываются: они относятся
	/// к новым дочерним объектам, а права самого каталога серверов задаёт демон.
	/// </summary>
	/// <param name="volumeRoot">
	/// Корень диска. Право «удаление» на нём ничего не даёт: корень тома нельзя удалить или
	/// переименовать. Windows при форматировании даёт прошедшим проверку «Изменение» на корень,
	/// и без этого исключения каталог серверов нельзя было бы создать ни на одном втором диске.
	/// </param>
	public static Phrase? Weakness(string path, FileSystemSecurity security, bool volumeRoot)
	{
		ArgumentNullException.ThrowIfNull(security);

		if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsTrusted(owner))
		{
			return Phrase.Format($"Владелец {path} — {Name(security.GetOwner(typeof(SecurityIdentifier)))}, а не SYSTEM или администраторы: он может подменить каталог серверов. Выберите каталог прямо в корне диска, например {path[..2]}\\vs-servers");
		}

		foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
		{
			if (rule.AccessControlType != AccessControlType.Allow
				|| rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)
				|| (rule.IdentityReference is SecurityIdentifier sid && IsTrusted(sid)))
			{
				continue;
			}

			var dangerous = dangerousRights | (volumeRoot ? 0 : (int) FileSystemRights.Delete);
			if (((int) rule.FileSystemRights & dangerous) != 0)
			{
				return Phrase.Format($"У {Name(rule.IdentityReference)} есть право удалять или менять разрешения в {path}: он может подменить каталог серверов, а игра работает от SYSTEM. Выберите каталог прямо в корне диска, например {path[..2]}\\vs-servers");
			}
		}

		return null;
	}

	/// <summary>Стоит ли уже список доступа, который ставит демон.</summary>
	public static bool IsProtected(FileSystemSecurity security)
	{
		ArgumentNullException.ThrowIfNull(security);
		return security.AreAccessRulesProtected
			&& security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && IsTrusted(owner)
			&& security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
				.Cast<FileSystemAccessRule>()
				.All(rule => rule.AccessControlType == AccessControlType.Deny || (rule.IdentityReference is SecurityIdentifier sid && IsTrusted(sid)));
	}

	/// <summary>
	/// Владелец SYSTEM: иначе у пользователя, заранее создавшего каталог, осталось бы право
	/// менять список доступа. Наследование от диска отключается.
	/// </summary>
	public static DirectorySecurity Protected()
	{
		var security = new DirectorySecurity();
		security.SetOwner(system);
		security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
		foreach (var sid in new[] { system, administrators })
		{
			security.AddAccessRule(new FileSystemAccessRule(
				sid,
				FileSystemRights.FullControl,
				InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
				PropagationFlags.None,
				AccessControlType.Allow));
		}

		return security;
	}

	private static bool IsTrusted(SecurityIdentifier sid) =>
		sid == system || sid == administrators || sid == trustedInstaller;

	/// <summary>Имя учётной записи для фразы; неизвестная — фраза, чтобы её перевели вместе с внешней.</summary>
	private static object Name(IdentityReference? identity)
	{
		if (identity is null)
		{
			return Phrase.From("неизвестная учётная запись");
		}

		try
		{
			return identity.Translate(typeof(NTAccount)).Value;
		}
		catch (Exception exception) when (exception is IdentityNotMappedException or UnauthorizedAccessException)
		{
			return identity.Value;
		}
	}
}
