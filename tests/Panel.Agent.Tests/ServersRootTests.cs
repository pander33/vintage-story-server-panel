using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Panel.Agent.Provisioning;
using Panel.Fleet.Infrastructure;
using Panel.Provisioning.Domain;
using Panel.SharedKernel;

namespace Panel.Agent.Tests;

/// <summary>
/// Смена каталога серверов на стороне демона: запись <c>vs-panel.json</c> и проверка предков.
/// </summary>
public sealed class ServersRootTests
{
	private const string windowsConfig = """
		{
		  "version": 2,
		  "layout": "windows",
		  "instances_file": "C:\\ProgramData\\vs-panel\\instances.json",
		  "instances_root": "C:\\ProgramData\\vs-panel\\servers",
		  "run_dir": "C:\\ProgramData\\vs-panel\\run"
		}
		""";

	private static ServersRoot Root(string path) =>
		ServersRoot.Create(path, PlatformLayout.Windows, [], []).Value;

	[Fact]
	public void Новый_каталог_записывается_а_прежний_запоминается()
	{
		var text = ProvisionBackend.RewriteInstancesRoot(windowsConfig, Root(@"D:\vs-servers"), @"C:\ProgramData\vs-panel\servers").Value;

		var configuration = PanelConfiguration.Parse(text).Value;
		Assert.Equal(@"D:\vs-servers", configuration.InstancesRoot);
		Assert.Equal([@"C:\ProgramData\vs-panel\servers"], configuration.FormerInstancesRoots);
		Assert.Equal(@"C:\ProgramData\vs-panel\run", configuration.RunDirectory);
		Assert.EndsWith("\n", text, StringComparison.Ordinal);
	}

	/// <summary>Остальные ключи и их порядок сохраняются.</summary>
	[Fact]
	public void Прочие_ключи_сохраняются_в_прежнем_порядке()
	{
		var text = ProvisionBackend.RewriteInstancesRoot(windowsConfig, Root(@"D:\vs-servers"), @"C:\ProgramData\vs-panel\servers").Value;

		var keys = ((JsonObject) JsonNode.Parse(text)!).Select(pair => pair.Key).ToList();
		Assert.Equal(["version", "layout", "instances_file", "instances_root", "run_dir", "former_instances_roots"], keys);
	}

	[Fact]
	public void Возврат_к_прежнему_каталогу_убирает_его_из_прежних()
	{
		var moved = ProvisionBackend.RewriteInstancesRoot(windowsConfig, Root(@"D:\vs-servers"), @"C:\ProgramData\vs-panel\servers").Value;

		var back = ProvisionBackend.RewriteInstancesRoot(moved, Root(@"c:\programdata\VS-PANEL\servers"), @"D:\vs-servers").Value;

		var configuration = PanelConfiguration.Parse(back).Value;
		Assert.Equal(@"c:\programdata\VS-PANEL\servers", configuration.InstancesRoot);
		Assert.Equal([@"D:\vs-servers"], configuration.FormerInstancesRoots);
	}

	[Fact]
	public void Прежний_каталог_не_повторяется()
	{
		var once = ProvisionBackend.RewriteInstancesRoot(windowsConfig, Root(@"D:\a"), @"C:\ProgramData\vs-panel\servers").Value;
		var twice = ProvisionBackend.RewriteInstancesRoot(once, Root(@"C:\ProgramData\vs-panel\servers2"), @"D:\a").Value;
		var thrice = ProvisionBackend.RewriteInstancesRoot(twice, Root(@"D:\b"), @"C:\ProgramData\vs-panel\servers2").Value;

		Assert.Equal(
			[@"C:\ProgramData\vs-panel\servers", @"D:\a", @"C:\ProgramData\vs-panel\servers2"],
			PanelConfiguration.Parse(thrice).Value.FormerInstancesRoots);
	}

	[Fact]
	public void Испорченный_файл_настроек_не_переписывается()
	{
		Assert.False(ProvisionBackend.RewriteInstancesRoot("{", Root(@"D:\a"), string.Empty).IsSuccess);
		Assert.False(ProvisionBackend.RewriteInstancesRoot("[]", Root(@"D:\a"), string.Empty).IsSuccess);
	}

	[Theory]
	[InlineData("0 755", 0, 0x1ED)]
	[InlineData("0 1777\n", 0, 0x3FF)]
	[InlineData("1000 700", 1000, 0x1C0)]
	public void Вывод_stat_разбирается(string output, int uid, int mode)
	{
		Assert.Equal((uid, mode), LinuxServersRoot.Parse(output));
	}

	[Theory]
	[InlineData("")]
	[InlineData("0")]
	[InlineData("root 755")]
	[InlineData("0 789")]
	[InlineData("0 17777")]
	public void Непонятный_вывод_stat_не_разбирается(string output)
	{
		Assert.Null(LinuxServersRoot.Parse(output));
	}

	[Theory]
	[InlineData(0, 0x1ED, true)]    // 0755
	[InlineData(0, 0x3FF, true)]    // 1777: sticky, как /tmp
	[InlineData(0, 0x1FD, false)]   // 0775: пишет группа
	[InlineData(0, 0x1FF, false)]   // 0777
	[InlineData(1000, 0x1ED, false)] // владелец не root
	public void Предок_на_Linux_судится_по_владельцу_и_записи(int uid, int mode, bool safe)
	{
		Assert.Equal(safe, LinuxServersRoot.Weakness("/data", uid, mode) is null);
	}

	[SupportedOSPlatform("windows")]
	private static SecurityIdentifier System => new(WellKnownSidType.LocalSystemSid, null);

	[SupportedOSPlatform("windows")]
	private static SecurityIdentifier Administrators => new(WellKnownSidType.BuiltinAdministratorsSid, null);

	[SupportedOSPlatform("windows")]
	private static SecurityIdentifier Users => new(WellKnownSidType.BuiltinUsersSid, null);

	[SupportedOSPlatform("windows")]
	private static SecurityIdentifier AuthenticatedUsers => new(WellKnownSidType.AuthenticatedUserSid, null);

	/// <summary>
	/// Корень диска D:\ после форматирования в Windows: «Изменение» для прошедших проверку на сам
	/// корень и дочерние объекты. Корень тома удалить нельзя, поэтому его право «удаление» не
	/// учитывается.
	/// </summary>
	[Fact]
	public void Корень_диска_по_умолчанию_безопасен()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		var security = new DirectorySecurity();
		security.SetOwner(System);
		security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
		security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
		security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
		security.AddAccessRule(new FileSystemAccessRule(AuthenticatedUsers, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
		security.AddAccessRule(new FileSystemAccessRule(AuthenticatedUsers, FileSystemRights.Modify, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));

		Assert.Null(WindowsServersRoot.Weakness(@"D:\", security, volumeRoot: true));
		Assert.NotNull(WindowsServersRoot.Weakness(@"D:\Games", security, volumeRoot: false));
	}

	/// <summary>Каталог, созданный пользователем в корне диска: унаследованное «Изменение» включает удаление.</summary>
	[Fact]
	public void Каталог_с_правом_изменения_для_пользователей_опасен()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		var security = new DirectorySecurity();
		security.SetOwner(Administrators);
		security.AddAccessRule(new FileSystemAccessRule(AuthenticatedUsers, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));

		Assert.NotNull(WindowsServersRoot.Weakness(@"D:\Games", security, volumeRoot: false));
	}

	[Fact]
	public void Каталог_во_владении_пользователя_опасен()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		var security = new DirectorySecurity();
		security.SetOwner(Users);

		Assert.Contains("Владелец", WindowsServersRoot.Weakness(@"D:\Games", security, volumeRoot: false)?.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public void Список_доступа_демона_узнаётся_как_свой()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		Assert.True(WindowsServersRoot.IsProtected(WindowsServersRoot.Protected()));
		Assert.Null(WindowsServersRoot.Weakness(@"D:\vs-servers", WindowsServersRoot.Protected(), volumeRoot: false));

		var open = WindowsServersRoot.Protected();
		open.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
		Assert.False(WindowsServersRoot.IsProtected(open));
	}

	/// <summary>
	/// Проверка на этой машине: предки каталога данных по умолчанию (<c>ProgramData</c> и корень
	/// системного диска) проходят проверку, иначе каталог серверов по умолчанию был бы
	/// отвергнут. Списки доступа только читаются.
	/// </summary>
	[Fact]
	public void Предки_ProgramData_проходят_проверку()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		for (var directory = new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)); directory is not null; directory = directory.Parent)
		{
			Assert.Null(WindowsServersRoot.Weakness(directory.FullName, directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), directory.Parent is null));
		}
	}

	/// <summary>Проверка на этой машине: корень каждого локального диска NTFS подходит в качестве родителя.</summary>
	[Fact]
	public void Корни_локальных_дисков_проходят_проверку()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed && drive.DriveFormat == "NTFS"))
		{
			Assert.Null(WindowsServersRoot.Weakness(drive.Name, drive.RootDirectory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), volumeRoot: true));
		}
	}
}
