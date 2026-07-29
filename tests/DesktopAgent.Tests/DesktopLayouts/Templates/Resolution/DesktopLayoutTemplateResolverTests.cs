using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Resolution;

public sealed class DesktopLayoutTemplateResolverTests
{
    private static NormalizedRect R(int l, int t, int r, int b) => new(l, t, r, b);
    private static DesktopLayoutZone Z(Guid id, string n, NormalizedRect b, int so,
        ZonePlacementFlow f = ZonePlacementFlow.ColumnMajor, int? max = null) =>
        new(id, n, b, so, f, max);

    private static DesktopLayoutTemplate T(Guid? tid, string n, TargetDisplayBinding? bnd, DesktopLayoutZone[] z) =>
        new(tid ?? Guid.NewGuid(), n, 1, bnd ?? TargetDisplayBinding.ForSelectedTarget(), z);

    private static ResolvedDesktopLayoutTemplate Resolve(DesktopLayoutTemplate t, PixelRect wa, PixelPoint sp) =>
        DesktopLayoutTemplateResolver.Resolve(t, wa, sp);

    private static readonly PixelPoint Sp80 = new(80, 80);

    // ═══ Full-range mapping ═══

    [Fact]
    public void FullRange_MapsToEntireWorkArea()
    {
        var t = T(null, "F", null, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) });
        var wa = new PixelRect(100, 200, 900, 600);
        var r = Resolve(t, wa, Sp80);
        var pb = r.Zones[0].PixelBounds;
        Assert.Equal(100, pb.Left);
        Assert.Equal(200, pb.Top);
        Assert.Equal(900, pb.Right);
        Assert.Equal(600, pb.Bottom);
    }

    [Fact]
    public void NormalizedZero_MapsToWorkAreaStart()
    {
        var t = T(null, "Z", null, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 5000, 5000), 0) });
        var wa = new PixelRect(-500, -300, 500, 300);
        var r = Resolve(t, wa, Sp80);
        Assert.Equal(-500, r.Zones[0].PixelBounds.Left);
        Assert.Equal(-300, r.Zones[0].PixelBounds.Top);
    }

    [Fact]
    public void Normalized10000_MapsToWorkAreaEnd()
    {
        var t = T(null, "Z", null, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) });
        var wa = new PixelRect(-500, -300, 500, 300);
        var r = Resolve(t, wa, Sp80);
        Assert.Equal(500, r.Zones[0].PixelBounds.Right);
        Assert.Equal(300, r.Zones[0].PixelBounds.Bottom);
    }

    // ═══ Negative work area coordinates ═══

    [Fact]
    public void NegativeLeft_WorkArea_Supported()
    {
        var t = T(null, "N", null, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) });
        var wa = new PixelRect(-1920, 0, 0, 1080);
        var r = Resolve(t, wa, Sp80);
        Assert.Equal(-1920, r.Zones[0].PixelBounds.Left);
        Assert.Equal(0, r.Zones[0].PixelBounds.Right);
    }

    [Fact]
    public void NegativeTop_WorkArea_Supported()
    {
        var t = T(null, "N", null, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) });
        var wa = new PixelRect(0, -1440, 2560, 0);
        var r = Resolve(t, wa, Sp80);
        Assert.Equal(-1440, r.Zones[0].PixelBounds.Top);
        Assert.Equal(0, r.Zones[0].PixelBounds.Bottom);
    }

    [Fact]
    public void WorkAreaStartNotZero_OffsetPreserved()
    {
        var t = T(null, "O", null, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 5000, 5000), 0) });
        var wa = new PixelRect(124, 0, 2560, 1440);
        var r = Resolve(t, wa, Sp80);
        Assert.Equal(124, r.Zones[0].PixelBounds.Left);
    }

    // ═══ Adjacent zones ═══

    [Fact]
    public void HalfHalf_Horizontal_Seamless()
    {
        var t = T(null, "H", null, new[]
        {
            Z(Guid.NewGuid(), "L", R(0, 0, 5000, 10000), 0),
            Z(Guid.NewGuid(), "R", R(5000, 0, 10000, 10000), 1),
        });
        var wa = new PixelRect(0, 0, 800, 600);
        var r = Resolve(t, wa, Sp80);
        Assert.Equal(0, r.Zones[0].PixelBounds.Left);
        Assert.Equal(r.Zones[0].PixelBounds.Right, r.Zones[1].PixelBounds.Left);
        Assert.Equal(800, r.Zones[1].PixelBounds.Right);
    }

    [Fact]
    public void HalfHalf_Vertical_Seamless()
    {
        var t = T(null, "V", null, new[]
        {
            Z(Guid.NewGuid(), "T", R(0, 0, 10000, 5000), 0),
            Z(Guid.NewGuid(), "B", R(0, 5000, 10000, 10000), 1),
        });
        var wa = new PixelRect(0, 0, 800, 600);
        var r = Resolve(t, wa, Sp80);
        Assert.Equal(0, r.Zones[0].PixelBounds.Top);
        Assert.Equal(r.Zones[0].PixelBounds.Bottom, r.Zones[1].PixelBounds.Top);
        Assert.Equal(600, r.Zones[1].PixelBounds.Bottom);
    }

    // ═══ Deterministic floor mapping ═══

    [Fact]
    public void Thirds_FloorMapping_Deterministic()
    {
        var t = T(null, "3", null, new[]
        {
            Z(Guid.NewGuid(), "A", R(0, 0, 3333, 10000), 0),
            Z(Guid.NewGuid(), "B", R(3333, 0, 6666, 10000), 1),
            Z(Guid.NewGuid(), "C", R(6666, 0, 10000, 10000), 2),
        });
        var wa = new PixelRect(0, 0, 1000, 500);
        // floor(3333*1000/10000) = floor(333.3) = 333
        // floor(6666*1000/10000) = floor(666.6) = 666
        var r = Resolve(t, wa, Sp80);
        Assert.Equal(0, r.Zones[0].PixelBounds.Left);
        Assert.Equal(333, r.Zones[0].PixelBounds.Right);
        Assert.Equal(333, r.Zones[1].PixelBounds.Left);
        Assert.Equal(666, r.Zones[1].PixelBounds.Right);
        Assert.Equal(666, r.Zones[2].PixelBounds.Left);
        Assert.Equal(1000, r.Zones[2].PixelBounds.Right);
    }

    [Fact]
    public void GapInNormalized_ZonesStillDontOverlap()
    {
        var t = T(null, "G", null, new[]
        {
            Z(Guid.NewGuid(), "A", R(0, 0, 4000, 10000), 0),
            Z(Guid.NewGuid(), "B", R(6000, 0, 10000, 10000), 1),
        });
        var wa = new PixelRect(0, 0, 1000, 500);
        var r = Resolve(t, wa, Sp80);
        Assert.False(DesktopAgent.Core.DesktopIcons.DesktopIconLayoutGeometry.Overlaps(
            r.Zones[0].PixelBounds, r.Zones[1].PixelBounds));
    }

    // ═══ Deterministic re-resolve ═══

    [Fact]
    public void ReResolve_SameResult()
    {
        var t = T(null, "R", null, new[]
        {
            Z(Guid.NewGuid(), "A", R(1000, 2000, 5000, 6000), 0),
            Z(Guid.NewGuid(), "B", R(6000, 2000, 9000, 6000), 1),
        });
        var wa = new PixelRect(0, 0, 1920, 1080);
        var r1 = Resolve(t, wa, Sp80);
        var r2 = Resolve(t, wa, Sp80);
        Assert.Equal(r1.Zones[0].PixelBounds, r2.Zones[0].PixelBounds);
        Assert.Equal(r1.Zones[1].PixelBounds, r2.Zones[1].PixelBounds);
    }

    // ═══ Collapse rejection ═══

    [Fact]
    public void NarrowZone_Collapses_Throws()
    {
        var t = T(null, "N", null, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 1, 10000), 0) });
        var wa = new PixelRect(0, 0, 500, 500);
        // floor(1*500/10000) = 0 → collapses
        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(t, wa, Sp80));
        Assert.Contains(t.TemplateId.ToString("D"), ex.Message);
        Assert.Contains("宽度为零", ex.Message);
    }

    [Fact]
    public void ShortZone_Collapses_Throws()
    {
        var t = T(null, "S", null, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 1), 0) });
        var wa = new PixelRect(0, 0, 500, 500);
        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(t, wa, Sp80));
        Assert.Contains("高度为零", ex.Message);
    }

    [Fact]
    public void CollapseException_ContainsTemplateAndZoneId()
    {
        var zid = Guid.NewGuid();
        var t = T(null, "C", null, new[] { Z(zid, "TestZone", R(0, 0, 1, 1), 0) });
        var wa = new PixelRect(0, 0, 100, 100);
        var ex = Assert.Throws<InvalidOperationException>(() => Resolve(t, wa, Sp80));
        Assert.Contains(t.TemplateId.ToString("D"), ex.Message);
        Assert.Contains(zid.ToString("D"), ex.Message);
        Assert.Contains("TestZone", ex.Message);
    }

    // ═══ No ceil expansion ═══

    [Fact]
    public void NotExpandingViaCeil()
    {
        // 1 unit at floor → width 0 → collapse. Shows floor is used.
        var t = T(null, "Ceil", null, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 1, 10000), 0) });
        var wa = new PixelRect(0, 0, 500, 500);
        Assert.Throws<InvalidOperationException>(() => Resolve(t, wa, Sp80));
    }

    // ═══ No overlap from rounding ═══

    [Fact]
    public void RoundingDoesNotCauseOverlap()
    {
        var t = T(null, "Rnd", null, new[]
        {
            Z(Guid.NewGuid(), "A", R(0, 0, 5000, 10000), 0),
            Z(Guid.NewGuid(), "B", R(5000, 0, 10000, 10000), 1),
        });
        var wa = new PixelRect(0, 0, 1001, 500);
        var r = Resolve(t, wa, Sp80);
        Assert.False(DesktopAgent.Core.DesktopIcons.DesktopIconLayoutGeometry.Overlaps(
            r.Zones[0].PixelBounds, r.Zones[1].PixelBounds));
    }

    // ═══ WorkArea validation ═══

    [Fact] public void ZeroWidthWA_Throws() { var t = T(null, "W", null, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); Assert.Throws<ArgumentOutOfRangeException>(() => Resolve(t, new PixelRect(0,0,0,500), Sp80)); }
    [Fact] public void ZeroHeightWA_Throws() { var t = T(null, "W", null, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); Assert.Throws<ArgumentOutOfRangeException>(() => Resolve(t, new PixelRect(0,0,500,0), Sp80)); }
    [Fact] public void ReversedWA_Throws() { var t = T(null, "W", null, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); Assert.Throws<ArgumentOutOfRangeException>(() => Resolve(t, new PixelRect(500,0,0,500), Sp80)); }

    // ═══ Spacing validation ═══

    [Fact] public void SpacingXZero_Throws() { var t = T(null, "S", null, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); Assert.Throws<ArgumentOutOfRangeException>(() => Resolve(t, new PixelRect(0,0,800,600), new PixelPoint(0, 80))); }
    [Fact] public void SpacingXNeg_Throws() { var t = T(null, "S", null, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); Assert.Throws<ArgumentOutOfRangeException>(() => Resolve(t, new PixelRect(0,0,800,600), new PixelPoint(-1, 80))); }
    [Fact] public void SpacingYZero_Throws() { var t = T(null, "S", null, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); Assert.Throws<ArgumentOutOfRangeException>(() => Resolve(t, new PixelRect(0,0,800,600), new PixelPoint(80, 0))); }
    [Fact] public void SpacingYNeg_Throws() { var t = T(null, "S", null, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); Assert.Throws<ArgumentOutOfRangeException>(() => Resolve(t, new PixelRect(0,0,800,600), new PixelPoint(80, -1))); }
    [Fact] public void NullTemplate_Throws() { Assert.Throws<ArgumentNullException>(() => Resolve(null!, new PixelRect(0,0,800,600), Sp80)); }
    [Fact] public void WAWidthOverflow_Throws() { var t = T(null, "O", null, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); Assert.Throws<ArgumentOutOfRangeException>(() => Resolve(t, new PixelRect(int.MinValue, 0, 0, 100), Sp80)); }
    [Fact] public void WAHeightOverflow_Throws() { var t = T(null, "O", null, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); Assert.Throws<ArgumentOutOfRangeException>(() => Resolve(t, new PixelRect(0, int.MinValue, 100, 0), Sp80)); }

    // ═══ Capacity: exact multiples ═══

    [Fact]
    public void ExactMultiple_CapacityCorrect()
    {
        var t = T(null, "C", null, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) });
        var wa = new PixelRect(0, 0, 800, 640); // 800/80=10, 640/80=8: true multiples
        var r = Resolve(t, wa, Sp80);
        Assert.Equal(10, r.Zones[0].ColumnCount);
        Assert.Equal(8, r.Zones[0].RowCount);
        Assert.Equal(80, r.Zones[0].TheoreticalCapacity);
    }

    [Fact]
    public void Remainder_Discarded()
    {
        var t = T(null, "R", null, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) });
        var wa = new PixelRect(0, 0, 850, 650);
        var r = Resolve(t, wa, Sp80);
        Assert.Equal(10, r.Zones[0].ColumnCount); // 850/80 = 10
        Assert.Equal(8, r.Zones[0].RowCount);     // 650/80 = 8
    }

    [Fact]
    public void ZoneSmallerThanSpacing_ZeroColumns()
    {
        var t = T(null, "Z", null, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) });
        var wa = new PixelRect(0, 0, 50, 50);
        var r = Resolve(t, wa, Sp80);
        Assert.Equal(0, r.Zones[0].ColumnCount);
        Assert.Equal(0, r.Zones[0].RowCount);
        Assert.Equal(0, r.Zones[0].TheoreticalCapacity);
    }

    [Fact]
    public void WidthBelowSpacing_HeightEnough_ZeroColumns_NonZeroRows()
    {
        var t = T(null, "W", null, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) });
        var wa = new PixelRect(0, 0, 50, 640); // 50<80, 640/80=8
        var r = Resolve(t, wa, Sp80);
        Assert.Equal(0, r.Zones[0].ColumnCount);
        Assert.Equal(8, r.Zones[0].RowCount);
        Assert.Equal(0, r.Zones[0].TheoreticalCapacity);
    }

    [Fact]
    public void HeightBelowSpacing_WidthEnough_ZeroRows_NonZeroColumns()
    {
        var t = T(null, "H", null, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) });
        var wa = new PixelRect(0, 0, 800, 50); // 800/80=10, 50<80
        var r = Resolve(t, wa, Sp80);
        Assert.Equal(10, r.Zones[0].ColumnCount);
        Assert.Equal(0, r.Zones[0].RowCount);
        Assert.Equal(0, r.Zones[0].TheoreticalCapacity);
    }

    [Fact]
    public void ZeroCapacityZone_IsValid()
    {
        var t = T(null, "Z", null, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) });
        var wa = new PixelRect(0, 0, 50, 50);
        var r = Resolve(t, wa, Sp80);
        Assert.False(r.Zones[0].HasCapacity);
        Assert.Equal(0, r.TotalTheoreticalCapacity);
    }

    // ═══ MaxItemCount ═══

    [Fact]
    public void MaxItemCount_Null_EffectiveEqualsTheoretical()
    {
        var t = T(null, "M", null, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: null) });
        var r = Resolve(t, new PixelRect(0, 0, 800, 600), Sp80);
        Assert.Equal(r.Zones[0].TheoreticalCapacity, r.Zones[0].EffectiveCapacity);
    }

    [Fact]
    public void MaxItemCount_LessThanTheoretical_Caps()
    {
        var t = T(null, "M", null, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 5) });
        var r = Resolve(t, new PixelRect(0, 0, 800, 600), Sp80);
        Assert.Equal(70, r.Zones[0].TheoreticalCapacity);
        Assert.Equal(5, r.Zones[0].EffectiveCapacity);
    }

    [Fact]
    public void MaxItemCount_GreaterThanTheoretical_DoesNotExpand()
    {
        var t = T(null, "M", null, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 999) });
        var r = Resolve(t, new PixelRect(0, 0, 800, 600), Sp80);
        Assert.Equal(70, r.Zones[0].TheoreticalCapacity);
        Assert.Equal(70, r.Zones[0].EffectiveCapacity);
    }

    [Fact]
    public void MaxItemCount_DoesNotChangeColRow()
    {
        var t = T(null, "M", null, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0, max: 3) });
        var r = Resolve(t, new PixelRect(0, 0, 800, 600), Sp80);
        Assert.Equal(10, r.Zones[0].ColumnCount);
        Assert.Equal(7, r.Zones[0].RowCount);
    }

    // ═══ Multi-zone totals ═══

    [Fact]
    public void MultiZone_TheoreticalTotal()
    {
        var t = T(null, "T", null, new[]
        {
            Z(Guid.NewGuid(), "A", R(0, 0, 5000, 10000), 0),
            Z(Guid.NewGuid(), "B", R(5000, 0, 10000, 10000), 1, max: 3),
        });
        var r = Resolve(t, new PixelRect(0, 0, 800, 600), Sp80);
        Assert.Equal(70, r.TotalTheoreticalCapacity);
        Assert.Equal(35 + 3, r.TotalEffectiveCapacity);
    }

    // ═══ Input order preserved ═══

    [Fact]
    public void Zones_PreserveInputOrder_NotSortOrder()
    {
        var t = T(null, "O", null, new[]
        {
            Z(Guid.NewGuid(), "Third", R(8000, 0, 10000, 5000), 2),
            Z(Guid.NewGuid(), "First", R(0, 0, 4000, 5000), 5),
            Z(Guid.NewGuid(), "Second", R(5000, 0, 7000, 5000), 1),
        });
        var r = Resolve(t, new PixelRect(0, 0, 1000, 500), Sp80);
        Assert.Equal("Third", r.Zones[0].Zone.Name);
        Assert.Equal("First", r.Zones[1].Zone.Name);
        Assert.Equal("Second", r.Zones[2].Zone.Name);
        Assert.Equal(2, r.Zones[0].Zone.SortOrder);
        Assert.Equal(5, r.Zones[1].Zone.SortOrder);
    }

    [Fact]
    public void PlacementFlow_Preserved()
    {
        var t = T(null, "F", null, new[]
        {
            Z(Guid.NewGuid(), "C", R(0, 0, 5000, 10000), 0, ZonePlacementFlow.ColumnMajor),
            Z(Guid.NewGuid(), "R", R(5000, 0, 10000, 10000), 1, ZonePlacementFlow.RowMajor),
        });
        var r = Resolve(t, new PixelRect(0, 0, 800, 600), Sp80);
        Assert.Equal(ZonePlacementFlow.ColumnMajor, r.Zones[0].Zone.PlacementFlow);
        Assert.Equal(ZonePlacementFlow.RowMajor, r.Zones[1].Zone.PlacementFlow);
    }

    // ═══ PixelBounds fully inside WorkArea ═══

    [Fact]
    public void PixelBounds_InsideWorkArea()
    {
        var t = T(null, "I", null, new[]
        {
            Z(Guid.NewGuid(), "A", R(1000, 1000, 9000, 9000), 0),
        });
        var wa = new PixelRect(0, 0, 1000, 1000);
        var r = Resolve(t, wa, Sp80);
        Assert.True(r.Zones[0].PixelBounds.Left >= 0);
        Assert.True(r.Zones[0].PixelBounds.Right <= 1000);
        Assert.True(r.Zones[0].PixelBounds.Top >= 0);
        Assert.True(r.Zones[0].PixelBounds.Bottom <= 1000);
    }

    // ═══ Immutability ═══

    [Fact]
    public void Zones_IsReadOnly()
    {
        var t = T(null, "R", null, new[] { Z(Guid.NewGuid(), "A", R(0, 0, 10000, 10000), 0) });
        var r = Resolve(t, new PixelRect(0, 0, 800, 600), Sp80);
        Assert.Throws<NotSupportedException>(() => ((IList<ResolvedDesktopLayoutZone>)r.Zones).Clear());
    }
}
