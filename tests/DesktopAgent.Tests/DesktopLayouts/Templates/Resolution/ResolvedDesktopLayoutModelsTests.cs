using DesktopAgent.Core.DesktopLayouts.Templates;
using DesktopAgent.Core.DesktopLayouts.Templates.Resolution;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Templates.Resolution;

public sealed class ResolvedDesktopLayoutModelsTests
{
    private static NormalizedRect R(int l, int t, int r, int b) => new(l, t, r, b);
    private static DesktopLayoutZone Z(Guid id, string n, NormalizedRect b, int so,
        ZonePlacementFlow f = ZonePlacementFlow.ColumnMajor, int? max = null) =>
        new(id, n, b, so, f, max);

    private static DesktopLayoutTemplate T(Guid? tid, string n, TargetDisplayBinding? bnd, DesktopLayoutZone[] z) =>
        new(tid ?? Guid.NewGuid(), n, 1, bnd ?? TargetDisplayBinding.ForSelectedTarget(), z);

    // ═══════════════════════════ ResolvedDesktopLayoutZone ═══════════════════════════

    [Fact] public void RZ_NullZone_Throws() { Assert.Throws<ArgumentNullException>(() => new ResolvedDesktopLayoutZone(null!, new PixelRect(0,0,800,640), 10, 8, 80, 80)); }

    [Fact] public void RZ_ZeroWidth_Throws() {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new ResolvedDesktopLayoutZone(Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0), new PixelRect(100,0,100,500), 0, 6, 0, 0));
        Assert.Contains("宽度", ex.Message); }

    [Fact] public void RZ_ReversedBounds_Throws() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResolvedDesktopLayoutZone(Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0), new PixelRect(500,0,100,500), 0, 0, 0, 0)); }

    [Fact] public void RZ_ReversedHeight_Throws() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResolvedDesktopLayoutZone(Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0), new PixelRect(0, 500, 800, 100), 0, 0, 0, 0)); }

    [Fact] public void RZ_WraparoundWidth_Throws() {
        // Left=1, Right=int.MinValue → overflow in int subtraction gives large positive, but long is correct
        // (long)int.MinValue - 1 = -2147483649 < 0 → rejected
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResolvedDesktopLayoutZone(Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0), new PixelRect(1, 0, int.MinValue, 500), 0, 0, 0, 0)); }

    [Fact] public void RZ_WidthExceedsIntMax_Throws() {
        // Left=int.MinValue, Right=0 → (long)0 - int.MinValue = 2147483648 > int.MaxValue
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResolvedDesktopLayoutZone(Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0), new PixelRect(int.MinValue, 0, 0, 500), 0, 0, 0, 0)); }

    [Fact] public void RZ_HeightExceedsIntMax_Throws() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResolvedDesktopLayoutZone(Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0), new PixelRect(0, int.MinValue, 500, 0), 0, 0, 0, 0)); }

    [Fact] public void RZ_NegativeColCount_Throws() { Assert.Throws<ArgumentOutOfRangeException>(() => new ResolvedDesktopLayoutZone(Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0), new PixelRect(0,0,800,640), -1, 8, -8, 0)); }
    [Fact] public void RZ_NegativeRowCount_Throws() { Assert.Throws<ArgumentOutOfRangeException>(() => new ResolvedDesktopLayoutZone(Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0), new PixelRect(0,0,800,640), 10, -1, -10, 0)); }
    [Fact] public void RZ_WrongTheoCapacity_Throws() { Assert.Throws<ArgumentOutOfRangeException>(() => new ResolvedDesktopLayoutZone(Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0), new PixelRect(0,0,800,640), 10, 8, 99, 99)); }
    [Fact] public void RZ_WrongEffCapacity_Throws() { Assert.Throws<ArgumentOutOfRangeException>(() => new ResolvedDesktopLayoutZone(Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0), new PixelRect(0,0,800,640), 10, 8, 80, 99)); }
    [Fact] public void RZ_NullMax_WrongEffCapacity_Throws() {
        var z = Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0, max: null);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResolvedDesktopLayoutZone(z, new PixelRect(0,0,800,640), 10, 8, 80, 50)); }

    [Fact] public void RZ_ValidZeroCapacity() {
        var rz = new ResolvedDesktopLayoutZone(Z(Guid.NewGuid(), "Z", R(0,0,10000,10000), 0), new PixelRect(0,0,50,50), 0, 0, 0, 0);
        Assert.False(rz.HasCapacity); Assert.Equal(0, rz.TheoreticalCapacity); Assert.Equal(0, rz.EffectiveCapacity); }

    [Fact] public void RZ_HasCapacity_Semantics() {
        var rz = new ResolvedDesktopLayoutZone(Z(Guid.NewGuid(), "C", R(0,0,10000,10000), 0), new PixelRect(0,0,800,640), 10, 8, 80, 80);
        Assert.True(rz.HasCapacity); }

    // ═══════════════════════════ ResolvedDesktopLayoutTemplate ═══════════════════════════

    private static ResolvedDesktopLayoutZone RZ(DesktopLayoutZone z, PixelRect pb, int col, int row, long theo, long eff) =>
        new(z, pb, col, row, theo, eff);

    [Fact] public void RT_NullTemplate_Throws() { Assert.Throws<ArgumentNullException>(() => new ResolvedDesktopLayoutTemplate(null!, new PixelRect(0,0,800,600), new PixelPoint(80,80), new[] { RZ(Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0), new PixelRect(0,0,800,600), 10, 7, 70, 70) })); }
    [Fact] public void RT_NullZones_Throws() { var t = T(null, "T", null, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); Assert.Throws<ArgumentNullException>(() => new ResolvedDesktopLayoutTemplate(t, new PixelRect(0,0,800,600), new PixelPoint(80,80), null!)); }
    // valid RZ helper for WorkArea tests — keeps RZ construction independent of WA
    private static ResolvedDesktopLayoutZone ValidRZ(DesktopLayoutZone z) =>
        RZ(z, new PixelRect(0, 0, 160, 160), 2, 2, 4, 4);

    [Fact] public void RT_ZeroWidthWA_Throws() { var t = T(null, "T", null, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new ResolvedDesktopLayoutTemplate(t, new PixelRect(0,0,0,600), new PixelPoint(80,80), new[] { ValidRZ(t.Zones[0]) })); Assert.Equal("workArea", ex.ParamName); }
    [Fact] public void RT_ReversedWA_Throws() { var t = T(null, "T", null, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new ResolvedDesktopLayoutTemplate(t, new PixelRect(500,0,0,600), new PixelPoint(80,80), new[] { ValidRZ(t.Zones[0]) })); Assert.Equal("workArea", ex.ParamName); }
    [Fact] public void RT_WAWidthOverIntMax_Throws() { var t = T(null, "T", null, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new ResolvedDesktopLayoutTemplate(t, new PixelRect(int.MinValue, 0, 0, 600), new PixelPoint(80,80), new[] { ValidRZ(t.Zones[0]) })); Assert.Equal("workArea", ex.ParamName); }
    [Fact] public void RT_WAHeightOverIntMax_Throws() { var t = T(null, "T", null, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new ResolvedDesktopLayoutTemplate(t, new PixelRect(0, int.MinValue, 800, 0), new PixelPoint(80,80), new[] { ValidRZ(t.Zones[0]) })); Assert.Equal("workArea", ex.ParamName); }
    [Fact] public void RT_BadSpacingX_Throws() { var t = T(null, "T", null, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); Assert.Throws<ArgumentOutOfRangeException>(() => new ResolvedDesktopLayoutTemplate(t, new PixelRect(0,0,800,600), new PixelPoint(0, 80), new[] { RZ(t.Zones[0], new PixelRect(0,0,800,600), 10, 7, 70, 70) })); }
    [Fact] public void RT_BadSpacingY_Throws() { var t = T(null, "T", null, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new ResolvedDesktopLayoutTemplate(t, new PixelRect(0,0,800,600), new PixelPoint(80, 0), new[] { RZ(t.Zones[0], new PixelRect(0,0,800,600), 10, 7, 70, 70) })); Assert.Equal("iconSpacing", ex.ParamName); }
    [Fact] public void RT_ZoneCountMismatch_Throws() { var t = T(null, "T", null, new[] { Z(Guid.NewGuid(), "A", R(0,0,5000,10000), 0), Z(Guid.NewGuid(), "B", R(5000,0,10000,10000), 1) }); Assert.Throws<ArgumentException>(() => new ResolvedDesktopLayoutTemplate(t, new PixelRect(0,0,800,600), new PixelPoint(80,80), new[] { RZ(t.Zones[0], new PixelRect(0,0,400,600), 5, 7, 35, 35) })); }
    [Fact] public void RT_NullZoneElement_Throws() { var t = T(null, "T", null, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) }); Assert.Throws<ArgumentException>(() => new ResolvedDesktopLayoutTemplate(t, new PixelRect(0,0,800,600), new PixelPoint(80,80), new ResolvedDesktopLayoutZone[] { null! })); }

    [Fact] public void RT_ZoneOrderMismatch_Throws() {
        var zA = Z(Guid.NewGuid(), "A", R(0,0,4000,10000), 0);
        var zB = Z(Guid.NewGuid(), "B", R(5000,0,10000,10000), 1);
        var t = T(null, "T", null, new[] { zA, zB });
        var rzB = RZ(zB, new PixelRect(400,0,800,600), 5, 7, 35, 35);
        var rzA = RZ(zA, new PixelRect(0,0,400,600), 5, 7, 35, 35);
        Assert.Throws<ArgumentException>(() => new ResolvedDesktopLayoutTemplate(t, new PixelRect(0,0,800,600), new PixelPoint(80,80), new[] { rzB, rzA })); }

    [Fact] public void RT_PixelBoundsOutOfWA_Throws() {
        var t = T(null, "T", null, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) });
        Assert.Throws<ArgumentException>(() => new ResolvedDesktopLayoutTemplate(t, new PixelRect(0,0,400,400), new PixelPoint(80,80), new[] { RZ(t.Zones[0], new PixelRect(0,0,800,600), 10, 7, 70, 70) })); }

    [Fact] public void RT_OverlappingPixelBounds_Throws() {
        var zA = Z(Guid.NewGuid(), "A", R(0,0,4000,10000), 0);
        var zB = Z(Guid.NewGuid(), "B", R(5000,0,10000,10000), 1);
        var t = T(null, "T", null, new[] { zA, zB });
        Assert.Throws<ArgumentException>(() => new ResolvedDesktopLayoutTemplate(t, new PixelRect(0,0,800,600), new PixelPoint(80,80), new[] { RZ(zA, new PixelRect(0,0,500,600), 6, 7, 42, 42), RZ(zB, new PixelRect(400,0,800,600), 5, 7, 35, 35) })); }

    [Fact] public void RT_WrongColCount_Throws() {
        var t = T(null, "T", null, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) });
        Assert.Throws<ArgumentException>(() => new ResolvedDesktopLayoutTemplate(t, new PixelRect(0,0,800,600), new PixelPoint(80,80), new[] { RZ(t.Zones[0], new PixelRect(0,0,800,600), 99, 7, 693, 693) })); }

    [Fact] public void RT_WrongRowCount_Throws() {
        var t = T(null, "T", null, new[] { Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0) });
        Assert.Throws<ArgumentException>(() => new ResolvedDesktopLayoutTemplate(t, new PixelRect(0,0,800,600), new PixelPoint(80,80), new[] { RZ(t.Zones[0], new PixelRect(0,0,800,600), 10, 99, 990, 990) })); }

    [Fact] public void RT_MutateInput_DoesNotAffectZones() {
        var zA = Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0);
        var t = T(null, "T", null, new[] { zA });
        var list = new List<ResolvedDesktopLayoutZone> { RZ(zA, new PixelRect(0,0,800,640), 10, 8, 80, 80) };
        var rt = new ResolvedDesktopLayoutTemplate(t, new PixelRect(0,0,800,640), new PixelPoint(80,80), list);
        list.Clear();
        Assert.Single(rt.Zones); }

    [Fact] public void RT_Zones_IsReadOnly() {
        var zA = Z(Guid.NewGuid(), "A", R(0,0,10000,10000), 0);
        var t = T(null, "T", null, new[] { zA });
        var rt = new ResolvedDesktopLayoutTemplate(t, new PixelRect(0,0,800,640), new PixelPoint(80,80), new[] { RZ(zA, new PixelRect(0,0,800,640), 10, 8, 80, 80) });
        Assert.Throws<NotSupportedException>(() => ((IList<ResolvedDesktopLayoutZone>)rt.Zones).Clear()); }

    [Fact] public void RT_MultiZone_TotalCapacityCorrect() {
        var zA = Z(Guid.NewGuid(), "A", R(0,0,5000,10000), 0);
        var zB = Z(Guid.NewGuid(), "B", R(5000,0,10000,10000), 1, max: 5);
        var t = T(null, "T", null, new[] { zA, zB });
        var rt = new ResolvedDesktopLayoutTemplate(t, new PixelRect(0,0,800,640), new PixelPoint(80,80), new[] { RZ(zA, new PixelRect(0,0,400,640), 5, 8, 40, 40), RZ(zB, new PixelRect(400,0,800,640), 5, 8, 40, 5) });
        Assert.Equal(80, rt.TotalTheoreticalCapacity);
        Assert.Equal(45, rt.TotalEffectiveCapacity); }
}
