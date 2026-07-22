using System.Diagnostics;
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
    public DesktopIconPositioningCapabilities GetCapabilities()
    {
        using var shellWindows = new ShellWindowsLateBound();
        var (ctx, connectionMethod, primaryEx) = DesktopShellIconReader.FindDesktopView(shellWindows);
        using (ctx)
        {
            var hr = ctx.FolderView.GetAutoArrange();
            if (hr < 0)
                throw new COMException("IFolderView2.GetAutoArrange failed", hr);

            bool isAutoArrange;
            if (hr == 0) // S_OK
                isAutoArrange = true;
            else if (hr == 1) // S_FALSE
                isAutoArrange = false;
            else
                throw new InvalidOperationException(
                    $"IFolderView2.GetAutoArrange 返回意外值：{hr}。无法判断自动排列状态。");

            hr = ctx.FolderView.GetSpacing(out Point spacing);
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
            // ── 自动排列保护（再次检查，不依赖旧 GetCapabilities 结果）──
            var hr = ctx.FolderView.GetAutoArrange();
            if (hr < 0)
                throw new COMException("IFolderView2.GetAutoArrange failed", hr);
            if (hr == 0) // S_OK
                throw new InvalidOperationException(
                    "桌面已启用「自动排列图标」。请先关闭自动排列后再执行移动操作。");

            // ── 枚举全部项目，通过 StableId 唯一匹配 PIDL ──
            hr = ctx.FolderView.ItemCount(SVGIO_ALLVIEW, out var itemCount);
            if (hr < 0) throw new COMException("IFolderView2.ItemCount failed", hr);
            if (itemCount <= 0)
                throw new InvalidOperationException("桌面没有图标，无法匹配 StableId。");

            hr = ctx.FolderView.Items(SVGIO_ALLVIEW, IidIEnumIDList, out var enumObj);
            if (hr < 0)
            {
                DesktopShellIconReader.SafeReleaseComObject(ref enumObj!);
                throw new COMException("IFolderView2.Items failed", hr);
            }
            if (enumObj is not IEnumIDList enumerator)
            {
                DesktopShellIconReader.SafeReleaseComObject(ref enumObj!);
                throw new InvalidOperationException("IFolderView2.Items did not return IEnumIDList.");
            }
            ctx.SetEnumerator(enumerator);

            nint matchedPidl = nint.Zero;
            var matchCount = 0;

            try
            {
                for (var i = 0; i < itemCount; i++)
                {
                    nint pidl = nint.Zero;
                    hr = enumerator.Next(1, out pidl, out var fetched);
                    if (hr < 0) throw new COMException("IEnumIDList.Next failed", hr);
                    if (hr == 1 || fetched == 0) break;

                    try
                    {
                        var descriptor = DesktopShellIconReader.ReadSingleIcon(
                            ctx.FolderView, ctx.DesktopFolder, pidl);

                        if (descriptor is null)
                        {
                            Marshal.FreeCoTaskMem(pidl);
                            throw new InvalidOperationException(
                                $"图标 #{i} 元数据读取失败，无法继续匹配。ReadSingleIcon 返回 null。");
                        }

                        if (string.Equals(descriptor.StableId, stableId, StringComparison.Ordinal))
                        {
                            matchCount++;
                            if (matchCount > 1)
                            {
                                Marshal.FreeCoTaskMem(pidl);
                                // 释放之前匹配的 PIDL
                                if (matchedPidl != nint.Zero)
                                {
                                    Marshal.FreeCoTaskMem(matchedPidl);
                                    matchedPidl = nint.Zero;
                                }
                                throw new InvalidOperationException(
                                    $"StableId \"{stableId}\" 在桌面视图中有多个匹配（至少 {matchCount} 个）。无法唯一确定目标图标。");
                            }
                            matchedPidl = pidl;
                        }
                        else
                        {
                            Marshal.FreeCoTaskMem(pidl);
                        }
                    }
                    catch (Exception ex) when (ex is not InvalidOperationException)
                    {
                        Marshal.FreeCoTaskMem(pidl);
                        throw new InvalidOperationException(
                            $"桌面图标 #{i} 元数据读取失败，无法继续 StableId 匹配。", ex);
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
                // 精确释放匹配 PIDL（仅一次）
                if (matchedPidl != nint.Zero)
                    Marshal.FreeCoTaskMem(matchedPidl);
            }
        }
    }
}
