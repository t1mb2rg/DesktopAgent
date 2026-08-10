using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DesktopAgent.WinUI;

/// <summary>
/// M5-00 feasibility-only notification-area host. It proves that the WinUI product shell can
/// stay alive while hidden and can be restored from the Windows notification area.
/// Product commands/menu UX remain M5-05 scope.
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    private const int GwlpWndProc = -4;
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint WmApp = 0x8000;
    private const uint TrayCallbackMessage = WmApp + 0x341;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const int IdiApplication = 32512;
    private const uint IconId = 1;

    private readonly IntPtr _hwnd;
    private readonly Action _restoreAction;
    private readonly WindowProcDelegate _windowProc;
    private IntPtr _previousWindowProc;
    private bool _iconAdded;
    private bool _disposed;

    public TrayIconHost(IntPtr hwnd, Action restoreAction)
    {
        if (hwnd == IntPtr.Zero)
            throw new ArgumentException("通知区域宿主需要有效 HWND。", nameof(hwnd));

        _hwnd = hwnd;
        _restoreAction = restoreAction ?? throw new ArgumentNullException(nameof(restoreAction));
        _windowProc = WindowProc;

        var callbackPointer = Marshal.GetFunctionPointerForDelegate(_windowProc);
        _previousWindowProc = SetWindowLongPtrW(_hwnd, GwlpWndProc, callbackPointer);
        if (_previousWindowProc == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法 subclass WinUI 主窗口以接收 tray callback。");

        try
        {
            AddIcon();
        }
        catch
        {
            RestoreWindowProcedure();
            throw;
        }
    }

    private void AddIcon()
    {
        var icon = LoadIconW(IntPtr.Zero, new IntPtr(IdiApplication));
        if (icon == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载 feasibility tray icon。");

        var data = CreateNotifyIconData(icon);
        if (!Shell_NotifyIconW(NimAdd, ref data))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Shell_NotifyIcon(NIM_ADD) 失败。");

        _iconAdded = true;
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == TrayCallbackMessage)
        {
            var mouseMessage = unchecked((uint)lParam.ToInt64());
            if (mouseMessage is WmLButtonUp or WmLButtonDoubleClick or WmRButtonUp)
            {
                _restoreAction();
                return IntPtr.Zero;
            }
        }

        return CallWindowProcW(_previousWindowProc, hwnd, message, wParam, lParam);
    }

    private NotifyIconData CreateNotifyIconData(IntPtr icon)
        => new()
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = icon,
            szTip = "DesktopAgent",
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_iconAdded)
        {
            var data = CreateNotifyIconData(IntPtr.Zero);
            _ = Shell_NotifyIconW(NimDelete, ref data);
            _iconAdded = false;
        }

        RestoreWindowProcedure();
        GC.SuppressFinalize(this);
    }

    private void RestoreWindowProcedure()
    {
        if (_previousWindowProc == IntPtr.Zero)
            return;

        _ = SetWindowLongPtrW(_hwnd, GwlpWndProc, _previousWindowProc);
        _previousWindowProc = IntPtr.Zero;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcDelegate(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIconW(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr newLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProcW(
        IntPtr previousWindowProc,
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);
}
