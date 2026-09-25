using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Panel.Cli;

/// <summary>Результат окна выбора каталога: <see cref="Shown"/> — окно открывалось; <see cref="Path"/> пуст, если нажата «Отмена».</summary>
internal sealed record FolderPick(bool Shown, string? Path)
{
	public static FolderPick Unavailable { get; } = new(false, null);
}

/// <summary>
/// Стандартное окно Windows «Выбор папки»: <c>IFileOpenDialog</c> с <c>FOS_PICKFOLDERS</c>, как
/// в проводнике. Без WinForms: установщик — консольная программа, и ради одного окна
/// библиотека интерфейса не нужна.
/// </summary>
/// <remarks>
/// <para>
/// Окно открывается в сеансе пользователя, запустившего <c>install.cmd</c>: процесс
/// установщика повышен, но рабочий стол тот же. Кнопка «Создать папку» создаёт каталог от
/// имени администратора; права на него потом всё равно ставит демон.
/// </para>
/// <para>
/// Если рабочего стола нет (удалённый сеанс без оболочки, Server Core), возвращается
/// <see cref="FolderPick.Unavailable"/>, и установщик спрашивает путь текстом.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class FolderPicker
{
	private const uint pickFolders = 0x20;
	private const uint forceFileSystem = 0x40;
	private const uint noChangeDirectory = 0x8;
	private const uint pathMustExist = 0x800;
	private const uint fileSystemPath = 0x80058000;
	private const int cancelled = unchecked((int) 0x800704C7);

	private static readonly Guid fileOpenDialogClass = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
	private static readonly Guid shellItemInterface = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

	/// <summary>Показать окно; вызовы идут в отдельном однопоточном подразделении, как требует оболочка.</summary>
	public static Task<FolderPick> PickAsync(string title, string? initial)
	{
		var result = new TaskCompletionSource<FolderPick>(TaskCreationOptions.RunContinuationsAsynchronously);
		var thread = new Thread(() => result.SetResult(Pick(title, initial))) { IsBackground = true };
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		return result.Task;
	}

	private static FolderPick Pick(string title, string? initial)
	{
		IFileDialog? dialog = null;
		try
		{
			var type = Type.GetTypeFromCLSID(fileOpenDialogClass, throwOnError: true)!;
			dialog = (IFileDialog) Activator.CreateInstance(type)!;
			dialog.GetOptions(out var options);
			dialog.SetOptions(options | pickFolders | forceFileSystem | pathMustExist | noChangeDirectory);
			dialog.SetTitle(title);
			dialog.SetOkButtonLabel("Выбрать");
			if (StartFolder(initial) is { } start && ItemFor(start) is { } item)
			{
				dialog.SetFolder(item);
			}

			var shown = dialog.Show(GetConsoleWindow());
			if (shown == cancelled)
			{
				return new FolderPick(true, null);
			}

			if (shown < 0)
			{
				return FolderPick.Unavailable;
			}

			dialog.GetResult(out var chosen);
			chosen.GetDisplayName(fileSystemPath, out var name);
			try
			{
				return new FolderPick(true, Marshal.PtrToStringUni(name));
			}
			finally
			{
				Marshal.FreeCoTaskMem(name);
			}
		}
		catch (Exception exception) when (exception is COMException or InvalidCastException or TypeLoadException)
		{
			return FolderPick.Unavailable;
		}
		finally
		{
			if (dialog is not null)
			{
				Marshal.ReleaseComObject(dialog);
			}
		}
	}

	/// <summary>Начальный каталог: сам каталог, если есть, иначе ближайший существующий предок.</summary>
	private static string? StartFolder(string? initial)
	{
		for (var directory = string.IsNullOrWhiteSpace(initial) ? null : new DirectoryInfo(initial); directory is not null; directory = directory.Parent)
		{
			if (directory.Exists)
			{
				return directory.FullName;
			}
		}

		return null;
	}

	private static IShellItem? ItemFor(string path)
	{
		var id = shellItemInterface;
		return SHCreateItemFromParsingName(path, IntPtr.Zero, ref id, out var item) >= 0 ? item : null;
	}

	[DllImport("kernel32.dll")]
	private static extern IntPtr GetConsoleWindow();

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid interfaceId, out IShellItem item);

	/// <summary>IModalWindow и IFileDialog: все слоты по порядку, у используемых — полная сигнатура.</summary>
	[ComImport]
	[Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IFileDialog
	{
		[PreserveSig]
		int Show(IntPtr owner);

		void SetFileTypes();

		void SetFileTypeIndex();

		void GetFileTypeIndex();

		void Advise();

		void Unadvise();

		void SetOptions(uint options);

		void GetOptions(out uint options);

		void SetDefaultFolder(IShellItem item);

		void SetFolder(IShellItem item);

		void GetFolder();

		void GetCurrentSelection();

		void SetFileName();

		void GetFileName();

		void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);

		void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);

		void SetFileNameLabel();

		void GetResult(out IShellItem item);
	}

	[ComImport]
	[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IShellItem
	{
		void BindToHandler();

		void GetParent();

		void GetDisplayName(uint form, out IntPtr name);
	}
}
