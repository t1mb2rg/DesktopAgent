using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Placement;
using DesktopAgent.Core.DesktopLayouts.Templates.Preview;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Preview;

public sealed class DesktopLayoutPreviewItemTests
{
    private static DesktopIconSnapshotItem I(int ord, string sid, int px = 0, int py = 0) =>
        new(ord, sid, $"N_{sid}", null, ItemKind.File, new PixelPoint(px, py), null, false, true);

    private static ResolvedDesktopLayoutZone RZ(DesktopLayoutZone z, PixelRect pb, int col, int row, long theo, long eff) =>
        new(z, pb, col, row, theo, eff);

    private static DesktopIconTargetPlacement TP(DesktopIconSnapshotItem icon, int slotIndex, PixelPoint spacing)
    {
        var z = new DesktopLayoutZone(Guid.NewGuid(), "Z", new NormalizedRect(0, 0, 10000, 10000), 0, ZonePlacementFlow.ColumnMajor, null);
        var rz = RZ(z, new PixelRect(0, 0, 800, 640), 10, 8, 80, 80);
        return new DesktopIconTargetPlacement(icon, rz, spacing, slotIndex);
    }

    private static readonly PixelPoint SP = new(80, 80);

    [Fact] public void IconNull_Fails() { var tp = TP(I(0, "a"), 0, SP); Assert.Throws<ArgumentNullException>(() => DesktopLayoutPreviewItem.FromPlacement(null!, tp)); }
    [Fact] public void PlacementNull_Fails() { Assert.Throws<ArgumentNullException>(() => DesktopLayoutPreviewItem.FromPlacement(I(0, "a"), null!)); }
    [Fact] public void SameInstance_Success() { var icon = I(0, "a"); var tp = TP(icon, 0, SP); var item = DesktopLayoutPreviewItem.FromPlacement(icon, tp); Assert.Equal(DesktopLayoutPreviewItemState.AlreadyAtTarget, item.State); }

    [Fact] public void SameOrdinalStableId_DifferentInstance_Fails()
    {
        var icon1 = I(0, "a");
        var icon2 = I(0, "a"); // 相同值但不同实例
        var tp = TP(icon1, 0, SP);
        Assert.Throws<ArgumentException>(() => DesktopLayoutPreviewItem.FromPlacement(icon2, tp));
    }

    [Fact] public void DifferentOrdinal_Fails() { var tp = TP(I(0, "a"), 0, SP); Assert.Throws<ArgumentException>(() => DesktopLayoutPreviewItem.FromPlacement(I(1, "a"), tp)); }
    [Fact] public void DifferentStableId_Fails() { var tp = TP(I(0, "a"), 0, SP); Assert.Throws<ArgumentException>(() => DesktopLayoutPreviewItem.FromPlacement(I(0, "wrong"), tp)); }
    [Fact] public void CaseDifferentStableId_Fails() { var tp = TP(I(0, "abc"), 0, SP); Assert.Throws<ArgumentException>(() => DesktopLayoutPreviewItem.FromPlacement(I(0, "ABC"), tp)); }
    [Fact] public void DifferentPixelPosition_Fails() { var tp = TP(I(0, "a", px: 100, py: 200), 0, SP); Assert.Throws<ArgumentException>(() => DesktopLayoutPreviewItem.FromPlacement(I(0, "a", px: 999, py: 888), tp)); }

    [Fact] public void AlreadyAtTarget_ExactFields()
    {
        var icon = I(0, "x", px: 0, py: 0);
        var tp = TP(icon, 0, SP); // slot0→(0,0)
        var item = DesktopLayoutPreviewItem.FromPlacement(icon, tp);
        Assert.Equal(DesktopLayoutPreviewItemState.AlreadyAtTarget, item.State);
        Assert.Equal(new PixelPoint(0, 0), item.CurrentPosition);
        Assert.Equal(new PixelPoint(0, 0), item.TargetPosition);
        Assert.Equal(0L, item.DeltaX);
        Assert.Equal(0L, item.DeltaY);
        Assert.True(item.IsAssigned);
        Assert.False(item.RequiresMove);
        Assert.Same(icon, item.Icon);
        Assert.Same(tp, item.Placement);
    }

    [Fact] public void MoveRequired_ExactFields()
    {
        var icon = I(1, "b", px: 80, py: 0);
        var tp = TP(icon, 0, SP); // slot0→(0,0)
        var item = DesktopLayoutPreviewItem.FromPlacement(icon, tp);
        Assert.Equal(DesktopLayoutPreviewItemState.MoveRequired, item.State);
        Assert.Equal(-80L, item.DeltaX);
        Assert.Equal(0L, item.DeltaY);
        Assert.True(item.IsAssigned);
        Assert.True(item.RequiresMove);
    }

    [Fact] public void PositiveDelta_Exact()
    {
        var icon = I(2, "c", px: 0, py: 0);
        var tp = TP(icon, 1, SP); // ColMajor slot1→col0,row1→(0,80)
        var item = DesktopLayoutPreviewItem.FromPlacement(icon, tp);
        Assert.Equal(0L, item.DeltaX);
        Assert.Equal(80L, item.DeltaY);
    }

    [Fact] public void NegativeDelta_Exact()
    {
        var icon = I(3, "d", px: 80, py: 80);
        var tp = TP(icon, 0, SP); // slot0→(0,0)
        var item = DesktopLayoutPreviewItem.FromPlacement(icon, tp);
        Assert.Equal(-80L, item.DeltaX);
        Assert.Equal(-80L, item.DeltaY);
    }

    [Fact] public void IntMinValue_Delta_Exact()
    {
        var icon = I(4, "e", px: int.MinValue, py: int.MinValue);
        var tp = TP(icon, 0, SP); // (0,0) - (int.MinValue, int.MinValue)
        var item = DesktopLayoutPreviewItem.FromPlacement(icon, tp);
        Assert.Equal(2147483648L, item.DeltaX);
        Assert.Equal(2147483648L, item.DeltaY);
    }

    [Fact] public void Unassigned_AllFields()
    {
        var icon = I(5, "f", px: 50, py: 60);
        var item = DesktopLayoutPreviewItem.ForUnassigned(icon);
        Assert.Equal(DesktopLayoutPreviewItemState.Unassigned, item.State);
        Assert.Null(item.Placement);
        Assert.Null(item.TargetPosition);
        Assert.Null(item.DeltaX);
        Assert.Null(item.DeltaY);
        Assert.False(item.IsAssigned);
        Assert.False(item.RequiresMove);
        Assert.Equal(new PixelPoint(50, 60), item.CurrentPosition);
    }
}
