using System.Drawing;
using System.Runtime.InteropServices;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.WindowsInterop.DesktopIcons;

/// <summary>
/// 只读 Shell 图标定位能力读取器。产品壳用它建立 DesktopObservation，
/// 无需构造 DesktopShellIconPositionWriter，也不会获得 MoveDesktopIcon 入口。
/// </summary>
public sealed class DesktopShellIconPositioningCapabilitiesReader
    : IDesktopIconPositioningCapabilitiesReader
{
    public DesktopIconPositioningCapabilities GetCapabilities()
    {
        using var shellWindows = new ShellWindowsLateBound();
        var (ctx, connectionMethod, primaryException) =
            DesktopShellIconReader.FindDesktopView(shellWindows);

        using (ctx)
        {
            var isAutoArrangeEnabled = ReadAutoArrangeState(ctx.FolderView);
            var hr = ctx.FolderView.GetSpacing(out Point spacing);
            if (hr < 0)
                throw new COMException("IFolderView2.GetSpacing failed", hr);
            if (spacing.X <= 0 || spacing.Y <= 0)
            {
                throw new InvalidOperationException(
                    $"图标间距无效：({spacing.X}, {spacing.Y})。X 和 Y 必须大于 0。");
            }

            return new DesktopIconPositioningCapabilities(
                isAutoArrangeEnabled,
                new PixelPoint(spacing.X, spacing.Y),
                connectionMethod,
                primaryException);
        }
    }

    private static bool ReadAutoArrangeState(IFolderView2Compat folderView)
    {
        var hr = folderView.GetAutoArrange();
        if (hr < 0)
            throw new COMException("IFolderView2.GetAutoArrange failed", hr);
        if (hr == 0)
            return true;
        if (hr == 1)
            return false;

        throw new InvalidOperationException(
            $"IFolderView2.GetAutoArrange 返回意外值：{hr}。无法判断自动排列状态。");
    }
}
