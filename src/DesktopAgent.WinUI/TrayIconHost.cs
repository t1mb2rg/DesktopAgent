using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DesktopAgent.WinUI;

public enum TrayIconCommand
{
    Open = 0,
    Layouts = 1,
    Pending = 2,
    Settings = 3,
    Exit = 4,
}

public interface ITrayCommandTarget
{
    void HandleTrayCommand(TrayIconCommand command);
}

/// <summary>
/// WinUI 产品壳的 Windows 通知区域宿主。
/// 左键恢复主窗口；右键提供少量高频产品入口，不承载 Writer 或自动化执行能力。
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    private const int GwlpWndProc = -4;
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint WmNull = 0x0000;
    private const uint WmApp = 0x8000;
    private const uint TrayCallbackMessage = WmApp + 0x341;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmNonotify = 0x0080;
    private const uint TpmReturnCmd = 0x0100;
    private const int IdiApplication = 32512;
    private const uint IconId = 1;

    private const uint OpenCommandId = 1001;
    private const uint LayoutsCommandId = 1002;
    private const uint PendingCommandId = 1003;
    private const uint SettingsCommandId = 1004;
    private const uint ExitCommandId = 1099;

    private readonly IntPtr _hwnd;
    private readonly Action<TrayIconCommand> _commandAction;
    private readonly WindowProcDelegate _windowProc;
    private IntPtr _previousWindowProc;
    private bool _iconAdded;
    private bool _disposed;

    public TrayIconHost(IntPtr hwnd, Action restoreAction)
        : this(hwnd, CreateCommandAction(restoreAction))
    {
    }

    public TrayIconHost(IntPtr hwnd, Action<TrayIconCommand> commandAction)
    {
        if (hwnd == IntPtr.Zero)
            throw new ArgumentException("通知区域宿主需要有效 HWND。", nameof(hwnd));

        _hwnd = hwnd;
        _commandAction = commandAction ?? throw new ArgumentNullException(nameof(commandAction));
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

    private static Action<TrayIconCommand> CreateCommandAction(Action restoreAction)
    {
        ArgumentNullException.ThrowIfNull(restoreAction);
        return command => DispatchLegacyRestoreTarget(restoreAction, command);
    }

    private static void DispatchLegacyRestoreTarget(Action restoreAction, TrayIconCommand command)
    {
        if (restoreAction.Target is ITrayCommandTarget commandTarget)
        {
            commandTarget.HandleTrayCommand(command);
            return;
        }

        if (command is not TrayIconCommand.Exit)
            restoreAction();
    }

    private void AddIcon()
    {
        var icon = LoadIconW(IntPtr.Zero, new IntPtr(IdiApplication));
        if (icon == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载 DesktopAgent tray icon。");

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
            switch (mouseMessage)
            {
                case WmLButtonUp:
                case WmLButtonDoubleClick:
                    _commandAction(TrayIconCommand.Open);
                    return IntPtr.Zero;

                case WmRButtonUp:
                    try
                    {
                        ShowContextMenu();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Tray context menu failed: {ex}");
                    }
                    return IntPtr.Zero;
            }
        }

        return CallWindowProcW(_previousWindowProc, hwnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 tray context menu。");

        try
        {
            AppendMenu(menu, MfString, OpenCommandId, "打开 DesktopAgent");
            _ = SetMenuDefaultItem(menu, OpenCommandId, byPosition: false);
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, LayoutsCommandId, "布局");
            AppendMenu(menu, MfString, PendingCommandId, "待确认");
            AppendMenu(menu, MfString, SettingsCommandId, "设置");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, ExitCommandId, "退出");

            if (!GetCursorPos(out var cursor))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 tray 菜单位置。");

            _ = SetForegroundWindow(_hwnd);
            var commandId = TrackPopupMenuEx(
                menu,
                TpmRightButton | TpmNonotify | TpmReturnCmd,
                cursor.X,
                cursor.Y,
                _hwnd,
                IntPtr.Zero);

            _ = PostMessageW(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);

            switch (commandId)
            {
                case OpenCommandId:
                    _commandAction(TrayIconCommand.Open);
                    break;
                case LayoutsCommandId:
                    _commandAction(TrayIconCommand.Layouts);
                    break;
                case PendingCommandId:
                    _commandAction(TrayIconCommand.Pending);
                    break;
                case SettingsCommandId:
                    _commandAction(TrayIconCommand.Settings);
                    break;
                case ExitCommandId:
                    _commandAction(TrayIconCommand.Exit);
                    break;
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private static void AppendMenu(IntPtr menu, uint flags, uint commandId, string? text)
    {
        if (!AppendMenuW(menu, flags, new UIntPtr(commandId), text))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 tray menu item。");
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(
        IntPtr menu,
        uint flags,
        UIntPtr newItemId,
        string? newItemText);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(
        IntPtr menu,
        uint flags,
        int x,
        int y,
        IntPtr hwnd,
        IntPtr parameters);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
