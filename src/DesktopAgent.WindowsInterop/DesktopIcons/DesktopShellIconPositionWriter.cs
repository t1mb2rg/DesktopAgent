using System.Drawing;
using System.Runtime.InteropServices;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Geometry;
using static DesktopAgent.WindowsInterop.DesktopIcons.NativeShellMethods;

namespace DesktopAgent.WindowsInterop.DesktopIcons;

/// <summary>
/// 基于 Shell COM IFolderView2 的单图标位置写入实现。
/// 复用 DesktopShellIconReader 的桌面视图连接和图标读取能力。
/// </summary>
public sealed class DesktopShellIconPositionWriter : IDesktopIconPositionWriter
{
    /// <summary>
    /// 统一解释 IFolderView2.GetAutoArrange 的 HRESULT。
    /// S_OK (0) → true, S_FALSE (1) → false, &lt;0 → COMException, 其他正值 → InvalidOperationException。
    /// </summary>
    private static bool ReadAutoArrangeState(IFolderView2Compat folderView)
    {
        var hr = folderView.GetAutoArrange();
        if (hr < 0)
            throw new COMException("IFolderView2.GetAutoArrange failed", hr);
        if (hr == 0) // S_OK
            return true;
        if (hr == 1) // S_FALSE
            return false;
        throw new InvalidOperationException(
            $"IFolderView2.GetAutoArrange 返回意外值：{hr}。无法判断自动排列状态。");
    }

    /// <summary>Items 返回的 COM 对象释放助手。</summary>
    private static void SafeReleaseObject(ref object? comObj)
    {
        if (comObj is not null && Marshal.IsComObject(comObj))
        {
            Marshal.ReleaseComObject(comObj);
            comObj = null;
        }
    }

    public DesktopIconPositioningCapabilities GetCapabilities()
    {
        using var shellWindows = new ShellWindowsLateBound();
        var (ctx, connectionMethod, primaryEx) = DesktopShellIconReader.FindDesktopView(shellWindows);
        using (ctx)
        {
            var isAutoArrange = ReadAutoArrangeState(ctx.FolderView);

            var hr = ctx.FolderView.GetSpacing(out Point spacing);
            if (hr < 0)
                throw new COMException("IFolderView2.GetSpacing failed", hr);

            if (spacing.X <= 0 || spacing.Y <= 0)
                throw new InvalidOperationException(
                    $"图标间距无效：({spacing.X}, {spacing.Y})。X 和 Y 必须大于 0。");

            return new DesktopIconPositioningCapabilities(
                isAutoArrange,
                new PixelPoint(spacing.X, spacing.Y),
                connectionMethod,
                primaryEx);
        }
    }

    public DesktopIconMoveResult MoveDesktopIcon(string stableId, PixelPoint targetPosition)
    {
        if (stableId is null)
            throw new ArgumentNullException(nameof(stableId));
        if (string.IsNullOrWhiteSpace(stableId))
            throw new ArgumentException("StableId 不能为空白。", nameof(stableId));

        using var shellWindows = new ShellWindowsLateBound();
        var (ctx, connectionMethod, primaryEx) = DesktopShellIconReader.FindDesktopView(shellWindows);
        using (ctx)
        {
            // ── 连接后第一次自动排列检查 ──
            if (ReadAutoArrangeState(ctx.FolderView))
                throw new InvalidOperationException(
                    "桌面已启用「自动排列图标」。请先关闭自动排列后再执行移动操作。");

            // ── 枚举全部项目，通过 StableId 唯一匹配 PIDL ──
            var hr = ctx.FolderView.ItemCount(SVGIO_ALLVIEW, out var itemCount);
            if (hr < 0) throw new COMException("IFolderView2.ItemCount failed", hr);
            if (itemCount <= 0)
                throw new InvalidOperationException("桌面没有图标，无法匹配 StableId。");

            hr = ctx.FolderView.Items(SVGIO_ALLVIEW, IidIEnumIDList, out var enumObj);
            if (hr < 0)
            {
                SafeReleaseObject(ref enumObj!);
                throw new COMException("IFolderView2.Items failed", hr);
            }
            if (enumObj is not IEnumIDList enumerator)
            {
                SafeReleaseObject(ref enumObj!);
                throw new InvalidOperationException("IFolderView2.Items did not return IEnumIDList.");
            }
            ctx.SetEnumerator(enumerator);

            nint matchedPidl = nint.Zero;
            var matchCount = 0;
            var enumIndex = 0;

            try
            {
                // 枚举到真正结束（不预设 itemCount），即使找到第一个匹配也需确认无第二个
                while (true)
                {
                    nint pidl = nint.Zero;
                    try
                    {
                        hr = enumerator.Next(1, out pidl, out var fetched);

                        if (hr < 0) throw new COMException("IEnumIDList.Next failed", hr);

                        if (hr == 1) // S_FALSE
                        {
                            if (fetched != 0)
                                throw new InvalidOperationException(
                                    $"IEnumIDList.Next 返回 S_FALSE 但 fetched={fetched}（预期 0）。");
                            break;
                        }

                        if (hr == 0) // S_OK
                        {
                            if (fetched != 1)
                                throw new InvalidOperationException(
                                    $"IEnumIDList.Next 返回 S_OK 但 fetched={fetched}（预期 1）。");
                        }
                        else
                        {
                            throw new InvalidOperationException(
                                $"IEnumIDList.Next 返回意外 HRESULT：0x{hr:X8}。");
                        }

                        enumIndex++;

                        // ── 内层异常边界：仅包裹 ReadSingleIcon ──
                        DesktopIconDescriptor descriptor;
                        try
                        {
                            descriptor = DesktopShellIconReader.ReadSingleIcon(
                                ctx.FolderView, ctx.DesktopFolder, pidl)
                                ?? throw new InvalidOperationException(
                                    "ReadSingleIcon 返回 null。");
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException(
                                $"桌面图标 #{enumIndex} 元数据读取失败，无法继续 StableId 唯一匹配。",
                                ex);
                        }

                        // ── 匹配逻辑放在内层 catch 之外 ──
                        if (string.Equals(descriptor.StableId, stableId, StringComparison.Ordinal))
                        {
                            matchCount++;
                            if (matchCount > 1)
                                throw new InvalidOperationException(
                                    $"StableId \"{stableId}\" 在桌面视图中有多个匹配（至少 {matchCount} 个）。无法唯一确定目标图标。");

                            // 所有权转移：matchedPidl 由外层 finally 释放，局部 pidl 不再由当前迭代 finally 释放
                            matchedPidl = pidl;
                            pidl = nint.Zero;
                        }
                    }
                    finally
                    {
                        if (pidl != nint.Zero)
                            Marshal.FreeCoTaskMem(pidl);
                    }
                }

                if (matchCount == 0)
                    throw new InvalidOperationException(
                        $"在桌面视图中未找到 StableId \"{stableId}\" 的图标。");
                if (matchCount != 1)
                    throw new InvalidOperationException(
                        $"StableId \"{stableId}\" 匹配了 {matchCount} 个图标（预期恰好 1 个）。");

                // ── 保存移动前位置 ──
                hr = ctx.FolderView.GetItemPosition(matchedPidl, out Point prevPt);
                if (hr < 0) throw new COMException("GetItemPosition (previous) failed", hr);
                var previousPosition = new PixelPoint(prevPt.X, prevPt.Y);

                // ── 移动前第二次自动排列检查（缩小竞态窗口）──
                if (ReadAutoArrangeState(ctx.FolderView))
                    throw new InvalidOperationException(
                        "桌面已启用「自动排列图标」（移动前检查）。请先关闭自动排列后再执行移动操作。");

                // ── 执行移动 ──
                var pidls = new[] { matchedPidl };
                var pts = new[] { new System.Drawing.Point(targetPosition.X, targetPosition.Y) };
                hr = ctx.FolderView.SelectAndPositionItems(1, pidls, pts, SVSI_POSITIONITEM);
                if (hr < 0) throw new COMException("SelectAndPositionItems failed", hr);

                // ── 读取移动后实际位置 ──
                hr = ctx.FolderView.GetItemPosition(matchedPidl, out Point actualPt);
                if (hr < 0) throw new COMException("GetItemPosition (actual) failed", hr);
                var actualPosition = new PixelPoint(actualPt.X, actualPt.Y);

                return new DesktopIconMoveResult(
                    stableId,
                    previousPosition,
                    targetPosition,
                    actualPosition,
                    connectionMethod,
                    primaryEx);
            }
            finally
            {
                if (matchedPidl != nint.Zero)
                    Marshal.FreeCoTaskMem(matchedPidl);
            }
        }
    }
}
