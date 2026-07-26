using DesktopAgent.Core.DesktopLayouts.Templates;

namespace DesktopAgent.Tests.DesktopLayouts.Templates;

public sealed class DesktopLayoutZoneTests
{
    private static NormalizedRect R(int l, int t, int r, int b) => new(l, t, r, b);

    // ═══ 正常构造 ═══

    [Fact]
    public void ValidZone_ConstructsSuccessfully()
    {
        var z = new DesktopLayoutZone(
            Guid.NewGuid(), "Main Zone", R(0, 0, 5000, 10000), 0,
            ZonePlacementFlow.ColumnMajor, maxItemCount: null);
        Assert.Equal("Main Zone", z.Name);
        Assert.Equal(0, z.SortOrder);
        Assert.Equal(ZonePlacementFlow.ColumnMajor, z.PlacementFlow);
        Assert.Null(z.MaxItemCount);
    }

    // ═══ ZoneId 验证 ═══

    [Fact]
    public void EmptyZoneId_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new DesktopLayoutZone(
                Guid.Empty, "Z", R(0, 0, 1000, 1000), 0,
                ZonePlacementFlow.ColumnMajor, null));
        Assert.Contains("ZoneId", ex.Message);
    }

    // ═══ Name 验证 ═══

    [Fact]
    public void NullName_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DesktopLayoutZone(
                Guid.NewGuid(), null!, R(0, 0, 1000, 1000), 0,
                ZonePlacementFlow.ColumnMajor, null));
    }

    [Fact]
    public void EmptyName_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new DesktopLayoutZone(
                Guid.NewGuid(), "", R(0, 0, 1000, 1000), 0,
                ZonePlacementFlow.ColumnMajor, null));
        Assert.Contains("Name", ex.Message);
    }

    [Fact]
    public void WhitespaceName_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new DesktopLayoutZone(
                Guid.NewGuid(), "   ", R(0, 0, 1000, 1000), 0,
                ZonePlacementFlow.ColumnMajor, null));
        Assert.Contains("Name", ex.Message);
    }

    // ═══ SortOrder 验证 ═══

    [Fact]
    public void NegativeSortOrder_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new DesktopLayoutZone(
                Guid.NewGuid(), "Z", R(0, 0, 1000, 1000), -1,
                ZonePlacementFlow.ColumnMajor, null));
        Assert.Contains("SortOrder", ex.Message);
    }

    [Fact]
    public void ZeroSortOrder_IsValid()
    {
        var z = new DesktopLayoutZone(
            Guid.NewGuid(), "Z", R(0, 0, 1000, 1000), 0,
            ZonePlacementFlow.ColumnMajor, null);
        Assert.Equal(0, z.SortOrder);
    }

    // ═══ PlacementFlow 验证 ═══

    [Fact]
    public void UndefinedPlacementFlow_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new DesktopLayoutZone(
                Guid.NewGuid(), "Z", R(0, 0, 1000, 1000), 0,
                (ZonePlacementFlow)99, null));
        Assert.Contains("PlacementFlow", ex.Message);
    }

    // ═══ MaxItemCount 验证 ═══

    [Fact]
    public void NullMaxItemCount_IsValid()
    {
        var z = new DesktopLayoutZone(
            Guid.NewGuid(), "Z", R(0, 0, 1000, 1000), 0,
            ZonePlacementFlow.ColumnMajor, maxItemCount: null);
        Assert.Null(z.MaxItemCount);
    }

    [Fact]
    public void ZeroMaxItemCount_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new DesktopLayoutZone(
                Guid.NewGuid(), "Z", R(0, 0, 1000, 1000), 0,
                ZonePlacementFlow.ColumnMajor, maxItemCount: 0));
        Assert.Contains("MaxItemCount", ex.Message);
    }

    [Fact]
    public void NegativeMaxItemCount_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new DesktopLayoutZone(
                Guid.NewGuid(), "Z", R(0, 0, 1000, 1000), 0,
                ZonePlacementFlow.ColumnMajor, maxItemCount: -5));
        Assert.Contains("MaxItemCount", ex.Message);
    }

    [Fact]
    public void PositiveMaxItemCount_IsValid()
    {
        var z = new DesktopLayoutZone(
            Guid.NewGuid(), "Z", R(0, 0, 1000, 1000), 0,
            ZonePlacementFlow.ColumnMajor, maxItemCount: 10);
        Assert.Equal(10, z.MaxItemCount);
    }

    // ═══ Bounds null 拒绝 ═══

    [Fact]
    public void NullBounds_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DesktopLayoutZone(
                Guid.NewGuid(), "Z", null!, 0,
                ZonePlacementFlow.ColumnMajor, null));
    }

    [Fact]
    public void DefaultNormalizedRect_IsNull_RejectedByZone()
    {
        // sealed record 的 default 为 null，无法传入 non-nullable Bounds 参数
        NormalizedRect? d = default;
        Assert.Null(d);
        // 编译时 null! 可绕过，运行时 Zone 拒绝
        Assert.Throws<ArgumentNullException>(
            () => new DesktopLayoutZone(
                Guid.NewGuid(), "Z", d!, 0,
                ZonePlacementFlow.ColumnMajor, null));
    }
}
