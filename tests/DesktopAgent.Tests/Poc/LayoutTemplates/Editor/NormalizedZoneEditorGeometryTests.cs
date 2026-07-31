using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Poc.LayoutTemplates.Editor;

namespace DesktopAgent.Tests.Poc.LayoutTemplates.Editor;

public sealed class NormalizedZoneEditorGeometryTests
{
    private static NormalizedRect R(int l, int t, int r, int b) => new(l, t, r, b);

    // ═══ SnapDelta ═══

    [Fact] public void SnapDelta_Positive_SnapsToNearest() { Assert.Equal(200, NormalizedZoneEditorGeometry.SnapDelta(180, 100)); }
    [Fact] public void SnapDelta_Negative_SnapsToNearest() { Assert.Equal(-200, NormalizedZoneEditorGeometry.SnapDelta(-180, 100)); }
    [Fact] public void SnapDelta_MidpointPositive_AwayFromZero() { Assert.Equal(200, NormalizedZoneEditorGeometry.SnapDelta(150, 100)); }
    [Fact] public void SnapDelta_MidpointNegative_AwayFromZero() { Assert.Equal(-200, NormalizedZoneEditorGeometry.SnapDelta(-150, 100)); }
    [Fact] public void SnapDelta_IllegalSnapStep_Throws() { Assert.Throws<ArgumentOutOfRangeException>(() => NormalizedZoneEditorGeometry.SnapDelta(10, 0)); Assert.Throws<ArgumentOutOfRangeException>(() => NormalizedZoneEditorGeometry.SnapDelta(10, -1)); }
    [Fact] public void SnapDelta_OddStep_1_Snaps_0() { Assert.Equal(0, NormalizedZoneEditorGeometry.SnapDelta(1, 3)); }
    [Fact] public void SnapDelta_OddStep_2_Snaps_3() { Assert.Equal(3, NormalizedZoneEditorGeometry.SnapDelta(2, 3)); }
    [Fact] public void SnapDelta_OddStep_Neg1_Snaps_0() { Assert.Equal(0, NormalizedZoneEditorGeometry.SnapDelta(-1, 3)); }
    [Fact] public void SnapDelta_OddStep_Neg2_Snaps_Neg3() { Assert.Equal(-3, NormalizedZoneEditorGeometry.SnapDelta(-2, 3)); }

    // ═══ Move ═══

    [Fact] public void Move_Normal_Shift() { var r = R(0, 0, 2000, 2000); var m = NormalizedZoneEditorGeometry.Move(r, 500, 500); Assert.Equal(500, m.Left); Assert.Equal(500, m.Top); Assert.Equal(2500, m.Right); Assert.Equal(2500, m.Bottom); }
    [Fact] public void Move_LeftClamp_Zero() { var r = R(200, 0, 2000, 2000); var m = NormalizedZoneEditorGeometry.Move(r, -500, 0); Assert.Equal(0, m.Left); }
    [Fact] public void Move_TopClamp_Zero() { var r = R(0, 200, 2000, 2000); var m = NormalizedZoneEditorGeometry.Move(r, 0, -500); Assert.Equal(0, m.Top); }
    [Fact] public void Move_RightClamp_Max() { var r = R(8000, 0, 10000, 2000); var m = NormalizedZoneEditorGeometry.Move(r, 500, 0); Assert.Equal(10000, m.Right); }
    [Fact] public void Move_BottomClamp_Max() { var r = R(0, 8000, 2000, 10000); var m = NormalizedZoneEditorGeometry.Move(r, 0, 500); Assert.Equal(10000, m.Bottom); }
    [Fact] public void Move_PreservesWidthHeight() { var r = R(1000, 1000, 4000, 3000); var m = NormalizedZoneEditorGeometry.Move(r, 2000, 3000); Assert.Equal(3000, m.Width); Assert.Equal(2000, m.Height); }
    [Fact] public void Move_LargeDelta_NoOverflow() { var r = R(0, 0, 5000, 5000); var m = NormalizedZoneEditorGeometry.Move(r, 10000, 10000); Assert.Equal(5000, m.Left); Assert.Equal(5000, m.Top); Assert.Equal(10000, m.Right); Assert.Equal(10000, m.Bottom); }
    [Fact] public void Move_NegativeDelta_NoOverflow() { var r = R(5000, 5000, 10000, 10000); var m = NormalizedZoneEditorGeometry.Move(r, -10000, -10000); Assert.Equal(0, m.Left); Assert.Equal(0, m.Top); }

    // ═══ Resize ═══

    [Fact] public void Resize_TopLeft() { var r = R(2000, 2000, 5000, 5000); var m = NormalizedZoneEditorGeometry.Resize(r, ZoneResizeHandle.TopLeft, -500, -500); Assert.Equal(1500, m.Left); Assert.Equal(1500, m.Top); Assert.Equal(5000, m.Right); Assert.Equal(5000, m.Bottom); }
    [Fact] public void Resize_TopRight() { var r = R(2000, 2000, 5000, 5000); var m = NormalizedZoneEditorGeometry.Resize(r, ZoneResizeHandle.TopRight, 500, -500); Assert.Equal(2000, m.Left); Assert.Equal(1500, m.Top); Assert.Equal(5500, m.Right); Assert.Equal(5000, m.Bottom); }
    [Fact] public void Resize_BottomLeft() { var r = R(2000, 2000, 5000, 5000); var m = NormalizedZoneEditorGeometry.Resize(r, ZoneResizeHandle.BottomLeft, -500, 500); Assert.Equal(1500, m.Left); Assert.Equal(2000, m.Top); Assert.Equal(5000, m.Right); Assert.Equal(5500, m.Bottom); }
    [Fact] public void Resize_BottomRight() { var r = R(2000, 2000, 5000, 5000); var m = NormalizedZoneEditorGeometry.Resize(r, ZoneResizeHandle.BottomRight, 500, 500); Assert.Equal(2000, m.Left); Assert.Equal(2000, m.Top); Assert.Equal(5500, m.Right); Assert.Equal(5500, m.Bottom); }
    [Fact] public void Resize_LeftBoundaryClamp() { var r = R(200, 0, 2000, 2000); var m = NormalizedZoneEditorGeometry.Resize(r, ZoneResizeHandle.TopLeft, -500, 0); Assert.Equal(0, m.Left); }
    [Fact] public void Resize_MinimumWidth_Enforced() { var r = R(500, 0, 1000, 2000); var m = NormalizedZoneEditorGeometry.Resize(r, ZoneResizeHandle.TopLeft, 600, 0, minimumSize: 500); Assert.True(m.Width >= 500); }
    [Fact] public void Resize_MinimumHeight_Enforced() { var r = R(0, 500, 2000, 1000); var m = NormalizedZoneEditorGeometry.Resize(r, ZoneResizeHandle.TopLeft, 0, 600, minimumSize: 500); Assert.True(m.Height >= 500); }
    [Fact] public void Resize_IllegalMinimumSize_Throws() { var r = R(0, 0, 2000, 2000); Assert.Throws<ArgumentOutOfRangeException>(() => NormalizedZoneEditorGeometry.Resize(r, ZoneResizeHandle.TopRight, 100, 100, minimumSize: 0)); }
    [Fact] public void Resize_IllegalHandle_Throws() { var r = R(0, 0, 2000, 2000); Assert.Throws<ArgumentOutOfRangeException>(() => NormalizedZoneEditorGeometry.Resize(r, (ZoneResizeHandle)99, 100, 100)); }
    [Fact] public void Resize_DoesNotProduceZeroArea() { var r = R(0, 0, 500, 500); var m = NormalizedZoneEditorGeometry.Resize(r, ZoneResizeHandle.TopLeft, 600, 600, minimumSize: 100); Assert.True(m.Width > 0); Assert.True(m.Height > 0); }
    [Fact] public void Resize_SourceSmallerThanMinimum_Throws() { var r = R(0, 0, 50, 50); Assert.Throws<ArgumentException>(() => NormalizedZoneEditorGeometry.Resize(r, ZoneResizeHandle.TopLeft, 10, 10, minimumSize: 100)); }

    // ═══ Overlap ═══

    [Fact] public void OverlapsAny_TrueOverlap() { var c = R(0, 0, 2000, 2000); var others = new[] { R(1000, 1000, 3000, 3000) }; Assert.True(NormalizedZoneEditorGeometry.OverlapsAny(c, others)); }
    [Fact] public void OverlapsAny_AdjacentLeftRight_NotOverlap() { var c = R(0, 0, 1000, 1000); var others = new[] { R(1000, 0, 2000, 1000) }; Assert.False(NormalizedZoneEditorGeometry.OverlapsAny(c, others)); }
    [Fact] public void OverlapsAny_AdjacentTopBottom_NotOverlap() { var c = R(0, 0, 1000, 1000); var others = new[] { R(0, 1000, 1000, 2000) }; Assert.False(NormalizedZoneEditorGeometry.OverlapsAny(c, others)); }
    [Fact] public void OverlapsAny_NullCollection_Throws() { Assert.Throws<ArgumentNullException>(() => NormalizedZoneEditorGeometry.OverlapsAny(R(0, 0, 1000, 1000), null!)); }
    [Fact] public void OverlapsAny_NullElement_Throws() { Assert.Throws<ArgumentException>(() => NormalizedZoneEditorGeometry.OverlapsAny(R(0, 0, 1000, 1000), new NormalizedRect[] { null! })); }

    // ═══ TryFindFreeRect ═══

    [Fact] public void TryFindFreeRect_Empty_Returns2500x2500_TopLeft() { var found = NormalizedZoneEditorGeometry.TryFindFreeRect(Array.Empty<NormalizedRect>(), out var r); Assert.True(found); Assert.NotNull(r); Assert.Equal(0, r!.Left); Assert.Equal(0, r.Top); Assert.Equal(2500, r.Right); Assert.Equal(2500, r.Bottom); }
    [Fact] public void TryFindFreeRect_SkipsOccupied() { var existing = new[] { R(0, 0, 2500, 2500) }; var found = NormalizedZoneEditorGeometry.TryFindFreeRect(existing, out var r); Assert.True(found); Assert.NotNull(r); }
    [Fact] public void TryFindFreeRect_DeterministicOrder_RepeatedCallsSame() { var existing = new[] { R(0, 0, 2500, 2500) }; NormalizedZoneEditorGeometry.TryFindFreeRect(existing, out var r1); NormalizedZoneEditorGeometry.TryFindFreeRect(existing, out var r2); Assert.Equal(r1, r2); }
    [Fact] public void TryFindFreeRect_FallsBackToSmallerSize()
    {
        // existing: [0,0,10000,9000) + [0,9000,9000,10000) = 仅剩 [9000,9000,10000,10000)
        // 2500 和 2000 无法放置，降级到 1000
        var existing = new[] { R(0, 0, 10000, 9000), R(0, 9000, 9000, 10000) };
        var found = NormalizedZoneEditorGeometry.TryFindFreeRect(existing, out var r);
        Assert.True(found);
        Assert.NotNull(r);
        Assert.Equal(9000, r!.Left);
        Assert.Equal(9000, r.Top);
        Assert.Equal(10000, r.Right);
        Assert.Equal(10000, r.Bottom);
    }
    [Fact] public void TryFindFreeRect_Full_ReturnsFalseNull() { var full = new[] { R(0, 0, 10000, 10000) }; var found = NormalizedZoneEditorGeometry.TryFindFreeRect(full, out var r); Assert.False(found); Assert.Null(r); }
    [Fact] public void TryFindFreeRect_DoesNotMutateInput() { var existing = new List<NormalizedRect> { R(0, 0, 2500, 2500) }; var before = existing.Count; NormalizedZoneEditorGeometry.TryFindFreeRect(existing, out _); Assert.Equal(before, existing.Count); }
}
