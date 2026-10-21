using DesktopAgent.Core.Geometry;
using static DesktopAgent.WindowsInterop.DesktopIcons.NativeShellMethods;

namespace DesktopAgent.WindowsInterop.DesktopIcons;

/// <summary>
/// 桌面图标视图的坐标空间。
///
/// 事实（真机实测，2026-10-14）：桌面图标列表视图（<c>Progman</c> →
/// <c>SHELLDLL_DefView</c> → <c>SysListView32</c>）横跨<strong>整个虚拟桌面</strong>：
/// 窗口矩形 (-1080,-210,2560,2520)，客户区原点在屏幕坐标 (-1080,-210)。
/// <c>IFolderView.GetItemPosition</c> 与 <c>SelectAndPositionItems</c> 用的都是这个
/// 「视图客户区坐标」，而产品其余部分（显示器 Bounds / WorkArea、区域矩形、计划、
/// 快照、判断归属）一律用<strong>屏幕坐标</strong>。
///
/// 多屏且主屏不在虚拟桌面左上角时这两个空间相差一个原点偏移；单屏或主屏在左上角时
/// 两者恰好相等，所以这个 bug 只在多屏机器上显形。本类型是两者之间<strong>唯一</strong>的换算点。
/// </summary>
public readonly record struct DesktopViewCoordinateSpace(PixelPoint ClientOrigin)
{
    /// <summary>视图原点即屏幕原点（单屏 / 主屏位于 (0,0)）。</summary>
    public static DesktopViewCoordinateSpace Identity => new(new PixelPoint(0, 0));

    /// <summary>取某个窗口客户区原点在屏幕坐标里的位置；拿不到就退化为虚拟桌面左上角。</summary>
    public static DesktopViewCoordinateSpace ForWindow(nint hwnd)
    {
        var origin = new System.Drawing.Point(0, 0);
        if (hwnd != nint.Zero && ClientToScreen(hwnd, ref origin))
            return new DesktopViewCoordinateSpace(new PixelPoint(origin.X, origin.Y));

        return FromVirtualScreen();
    }

    /// <summary>虚拟桌面左上角（多屏时通常为负）。</summary>
    public static DesktopViewCoordinateSpace FromVirtualScreen()
        => new(new PixelPoint(
            GetSystemMetrics(SM_XVIRTUALSCREEN),
            GetSystemMetrics(SM_YVIRTUALSCREEN)));

    /// <summary>视图坐标 → 屏幕坐标。</summary>
    public PixelPoint ToScreen(PixelPoint viewPoint)
        => new(viewPoint.X + ClientOrigin.X, viewPoint.Y + ClientOrigin.Y);

    /// <summary>屏幕坐标 → 视图坐标（写图标位置时用）。</summary>
    public PixelPoint ToView(PixelPoint screenPoint)
        => new(screenPoint.X - ClientOrigin.X, screenPoint.Y - ClientOrigin.Y);
}
