using System;
using System.Runtime.InteropServices;

namespace DesktopAgent.WinUI;

/// <summary>
/// 产品图标唯一出口。
/// </summary>
/// <remarks>
/// 图标编译进可执行文件（csproj 的 <c>ApplicationIcon</c>），运行期从 exe 文件自身提取，
/// 因此不需要额外文件随包分发，也不依赖资源 ID 的具体取值。
/// 取到的句柄在进程生命周期内缓存且不销毁：托盘与标题栏共用同一份。
/// 任何一步失败都只降级为"没有自定义图标"，绝不抛异常 —— 图标不是关键路径。
/// </remarks>
internal static class ProductIcon
{
    private const uint WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;

    private static IntPtr _small;
    private static IntPtr _large;
    private static bool _attempted;
    private static bool _reportedMissing;

    /// <summary>托盘与任务栏用小图标。可能为 <see cref="IntPtr.Zero"/>，调用方必须容忍。</summary>
    public static IntPtr Small
    {
        get { EnsureLoaded(); return _small; }
    }

    /// <summary>窗口标题栏与 Alt+Tab 用大图标。可能为 <see cref="IntPtr.Zero"/>。</summary>
    public static IntPtr Large
    {
        get { EnsureLoaded(); return _large; }
    }

    /// <summary>把产品图标挂到窗口上。失败只记录，不影响启动。</summary>
    public static void ApplyTo(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        TrySetWindowIcon(hwnd, IconBig, Large);
        TrySetWindowIcon(hwnd, IconSmall, Small);
    }

    private static void TrySetWindowIcon(IntPtr hwnd, int iconKind, IntPtr icon)
    {
        if (icon == IntPtr.Zero)
            return;

        try
        {
            SendMessageW(hwnd, WmSetIcon, new IntPtr(iconKind), icon);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WM_SETICON failed: {ex}");
        }
    }

    private static void EnsureLoaded()
    {
        if (_attempted)
            return;

        _attempted = true;

        try
        {
            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path))
            {
                ReportMissing();
                return;
            }

            // index 0 = 可执行文件里的第一个图标组。这比猜 RT_GROUP_ICON 的资源 ID 稳。
            if (ExtractIconExW(path, 0, out var large, out var small, 1) == 0)
            {
                ReportMissing();
                return;
            }

            _large = large;
            _small = small != IntPtr.Zero ? small : large;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Product icon extraction failed: {ex}");
            ReportMissing();
        }
    }

    private static void ReportMissing()
    {
        if (_reportedMissing)
            return;

        _reportedMissing = true;
        System.Diagnostics.Debug.WriteLine(
            "Product icon resource not found; falling back to the system default.");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
