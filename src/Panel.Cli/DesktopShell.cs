using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Panel.Cli;

/// <summary>
/// Открыть адрес от имени пользователя, а не установщика, чтобы браузер не получил права
/// администратора.
/// </summary>
/// <remarks>
/// <para>
/// Установщик работает с повышенными правами, и браузер, запущенный им напрямую, получил бы
/// их же. <c>explorer.exe &lt;адрес&gt;</c> недокументирован и на Windows 11 открыл
/// «Документы» вместо страницы. Здесь адрес открывает работающая оболочка рабочего стола
/// через <c>IShellDispatch2.ShellExecute</c>, как в заметке Раймонда Чена «How can I launch
/// an unelevated process from my elevated process». Программа запускается в процессе
/// оболочки, от имени вошедшего пользователя и без повышения.
/// </para>
/// <para>
/// Если оболочки нет (вход без рабочего стола, удалённый сеанс без Explorer), возвращается
/// <see langword="false"/>, и ссылка остаётся в выводе. Запускать браузер с правами
/// администратора вместо этого нельзя.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class DesktopShell
{
	private const int desktopWindowClass = 8;
	private const int needDispatch = 1;
	private const int showNormal = 1;
	private const int backgroundItem = 0;

	private static readonly Guid shellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
	private static readonly Guid topLevelBrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
	private static readonly Guid shellBrowserInterface = new("000214E2-0000-0000-C000-000000000046");
	private static readonly Guid dispatchInterface = new("00020400-0000-0000-C000-000000000046");

	/// <summary>
	/// Открыть <paramref name="target"/> оболочкой пользователя; при неудаче <see langword="false"/>.
	/// Вызовы оболочки идут в отдельном однопоточном подразделении, как требует Explorer.
	/// </summary>
	public static Task<bool> OpenAsync(string target)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(target);
		var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var thread = new Thread(() => result.SetResult(Open(target))) { IsBackground = true };
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
		return result.Task;
	}

	private static bool Open(string target)
	{
		try
		{
			var type = Type.GetTypeFromCLSID(shellWindowsClass, throwOnError: true)!;
			var windows = (IShellWindows) Activator.CreateInstance(type)!;
			object location = 0;
			object root = null!;
			var desktop = windows.FindWindowSW(ref location, ref root, desktopWindowClass, out _, needDispatch);
			if (desktop is null)
			{
				return false;
			}

			var service = topLevelBrowserService;
			var browserInterface = shellBrowserInterface;
			((IServiceProvider) desktop).QueryService(ref service, ref browserInterface, out var browserObject);
			var view = ((IShellBrowser) browserObject).QueryActiveShellView();
			var dispatchInterface = DesktopShell.dispatchInterface;
			dynamic folderView = view.GetItemObject(backgroundItem, ref dispatchInterface);
			dynamic shell = folderView.Application;
			shell.ShellExecute(target, string.Empty, string.Empty, "open", showNormal);
			return true;
		}
		catch (Exception exception) when (exception is COMException or InvalidCastException or InvalidComObjectException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or TypeLoadException)
		{
			return false;
		}
	}

	/// <summary>Слоты до <c>FindWindowSW</c> нужны только для порядка таблицы методов.</summary>
	[ComImport]
	[Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
	[InterfaceType(ComInterfaceType.InterfaceIsDual)]
	private interface IShellWindows
	{
		void GetCount();

		void Item();

		void NewEnum();

		void Register();

		void RegisterPending();

		void Revoke();

		void OnNavigate();

		void OnActivated();

		[return: MarshalAs(UnmanagedType.IDispatch)]
		object? FindWindowSW(ref object location, ref object root, int windowClass, out int window, int options);
	}

	[ComImport]
	[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IServiceProvider
	{
		void QueryService(ref Guid service, ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object result);
	}

	/// <summary>Слоты IOleWindow и IShellBrowser до <c>QueryActiveShellView</c>.</summary>
	[ComImport]
	[Guid("000214E2-0000-0000-C000-000000000046")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IShellBrowser
	{
		void GetWindow();

		void ContextSensitiveHelp();

		void InsertMenusSB();

		void SetMenuSB();

		void RemoveMenusSB();

		void SetStatusTextSB();

		void EnableModelessSB();

		void TranslateAcceleratorSB();

		void BrowseObject();

		void GetViewStateStream();

		void GetControlWindow();

		void SendControlMsg();

		IShellView QueryActiveShellView();
	}

	/// <summary>Слоты IOleWindow и IShellView до <c>GetItemObject</c>.</summary>
	[ComImport]
	[Guid("000214E3-0000-0000-C000-000000000046")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IShellView
	{
		void GetWindow();

		void ContextSensitiveHelp();

		void TranslateAccelerator();

		void EnableModeless();

		void UIActivate();

		void Refresh();

		void CreateViewWindow();

		void DestroyViewWindow();

		void GetCurrentInfo();

		void AddPropertySheetPages();

		void SaveViewState();

		void SelectItem();

		[return: MarshalAs(UnmanagedType.IDispatch)]
		object GetItemObject(uint item, ref Guid interfaceId);
	}
}
