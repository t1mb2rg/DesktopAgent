using System.Security.Cryptography;
using System.Text;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Blueprints;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Compilation;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Geometry;

namespace DesktopAgent.Application.DesktopLayouts.Workspaces;

/// <summary>将冻结编译结果投影为只读线框，不生成布局草稿或执行权限。</summary>
public static class DesktopWorkspaceOverlayProjection
{
    public static IReadOnlyList<DesktopLayoutZone> Project(IEnumerable<CompiledWorkspaceZone> zones, ExplorerDisplayGeometry display)
        => zones.OrderBy(zone => zone.ZoneKey, StringComparer.Ordinal).Select((zone, index) =>
        {
            var pixels = ExplorerGridPresentation.FrameBounds(zone.NativeRect, display);
            var work = display.WorkArea;
            int X(int value) => Math.Clamp((int)Math.Round((value - work.Left) * (double)NormalizedRect.Scale / work.Width), 0, NormalizedRect.Scale);
            int Y(int value) => Math.Clamp((int)Math.Round((value - work.Top) * (double)NormalizedRect.Scale / work.Height), 0, NormalizedRect.Scale);
            return new DesktopLayoutZone(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(zone.DisplayRef + "\n" + zone.ZoneKey)).AsSpan(0, 16)),
                zone.Name, new(X(pixels.Left), Y(pixels.Top), X(pixels.Right), Y(pixels.Bottom)), index,
                zone.PackingOrder == DesktopWorkspacePackingOrder.RowMajor ? ZonePlacementFlow.RowMajor : ZonePlacementFlow.ColumnMajor, maxItemCount: null);
        }).ToArray();
    public static IReadOnlyList<DesktopLayoutZone> Project(IEnumerable<CompiledWorkspaceZone> zones)
        => zones.OrderBy(static zone => zone.ZoneKey, StringComparer.Ordinal)
            .Select((zone, index) => new DesktopLayoutZone(
                new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(zone.DisplayRef + "\n" + zone.ZoneKey)).AsSpan(0, 16)),
                zone.Name,
                Bounds(zone.RealizedBounds),
                index,
                zone.PackingOrder == DesktopWorkspacePackingOrder.RowMajor
                    ? ZonePlacementFlow.RowMajor : ZonePlacementFlow.ColumnMajor,
                maxItemCount: null)).ToArray();

    private static NormalizedRect Bounds(DesktopWorkspaceRelativeRect bounds)
    {
        static int Scale(double value) => (int)Math.Round(value * NormalizedRect.Scale, MidpointRounding.AwayFromZero);
        return new NormalizedRect(Scale(bounds.Left), Scale(bounds.Top),
            Scale(bounds.Left + bounds.Width), Scale(bounds.Top + bounds.Height));
    }
}
