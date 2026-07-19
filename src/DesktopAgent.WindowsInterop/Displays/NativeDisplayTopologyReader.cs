using System.ComponentModel;
using System.Runtime.InteropServices;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.WindowsInterop.Displays;

/// <summary>
/// 通过公开 Win32 API 枚举当前所有显示器，并返回虚拟桌面物理像素坐标。
/// </summary>
public sealed class NativeDisplayTopologyReader : IDisplayTopologyReader
{
    private const uint MonitorInfoPrimary = 0x00000001;
    private const int DefaultDpi = 96;
    private const int MdtEffectiveDpi = 0;

    public IReadOnlyList<DisplayDescriptor> GetDisplays()
    {
        var displays = new List<DisplayDescriptor>();

        bool Callback(nint monitor, nint hdc, ref NativeRect monitorRect, nint data)
        {
            var info = MonitorInfoEx.Create();

            if (!GetMonitorInfo(monitor, ref info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取显示器信息。");
            }

            GetMonitorDpi(monitor, out var dpiX, out var dpiY);

            displays.Add(new DisplayDescriptor(
                info.DeviceName,
                ToPixelRect(info.Monitor),
                ToPixelRect(info.WorkArea),
                dpiX,
                dpiY,
                (info.Flags & MonitorInfoPrimary) != 0));

            return true;
        }

        if (!EnumDisplayMonitors(nint.Zero, nint.Zero, Callback, nint.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法枚举显示器。");
        }

        return displays
            .OrderByDescending(display => display.IsPrimary)
            .ThenBy(display => display.Bounds.Left)
            .ThenBy(display => display.Bounds.Top)
            .ToArray();
    }

    private static void GetMonitorDpi(nint monitor, out uint dpiX, out uint dpiY)
    {
        try
        {
            var result = GetDpiForMonitor(monitor, MdtEffectiveDpi, out dpiX, out dpiY);
            if (result == 0)
            {
                return;
            }
        }
        catch (DllNotFoundException)
        {
            // Windows 8.1 以下系统没有 Shcore.dll；本项目当前面向 Windows 11，
            // 但仍保留安全回退以避免读取失败导致应用崩溃。
        }
        catch (EntryPointNotFoundException)
        {
        }

        dpiX = DefaultDpi;
        dpiY = DefaultDpi;
    }

    private static PixelRect ToPixelRect(NativeRect rect) =>
        new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private delegate bool MonitorEnumProc(
        nint monitor,
        nint hdc,
        ref NativeRect monitorRect,
        nint data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(
        nint hdc,
        nint clipRect,
        MonitorEnumProc callback,
        nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        nint monitor,
        ref MonitorInfoEx monitorInfo);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        nint monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        private const int DeviceNameLength = 32;

        public uint Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DeviceNameLength)]
        public string DeviceName;

        public static MonitorInfoEx Create() => new()
        {
            Size = (uint)Marshal.SizeOf<MonitorInfoEx>(),
            DeviceName = string.Empty,
        };
    }
}
