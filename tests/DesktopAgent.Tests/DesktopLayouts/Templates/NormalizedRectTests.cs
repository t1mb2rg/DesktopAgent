using DesktopAgent.Core.DesktopLayouts.Templates;

namespace DesktopAgent.Tests.DesktopLayouts.Templates;

public sealed class NormalizedRectTests
{
    // ═══ 构造合法 ═══

    [Fact]
    public void FullRange_IsValid()
    {
        var r = new NormalizedRect(0, 0, NormalizedRect.Scale, NormalizedRect.Scale);
        Assert.Equal(0, r.Left);
        Assert.Equal(0, r.Top);
        Assert.Equal(NormalizedRect.Scale, r.Right);
        Assert.Equal(NormalizedRect.Scale, r.Bottom);
    }

    [Fact]
    public void SubRegion_IsValid()
    {
        var r = new NormalizedRect(1000, 2000, 5000, 6000);
        Assert.Equal(1000, r.Left);
        Assert.Equal(5000, r.Right);
    }

    // ═══ 边界拒绝 ═══

    [Fact]
    public void LeftNegative_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new NormalizedRect(-1, 0, 100, 100));
        Assert.Contains("Left", ex.Message);
    }

    [Fact]
    public void TopNegative_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new NormalizedRect(0, -5, 100, 100));
        Assert.Contains("Top", ex.Message);
    }

    [Fact]
    public void RightExceedsScale_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new NormalizedRect(0, 0, NormalizedRect.Scale + 1, 100));
        Assert.Contains("Right", ex.Message);
    }

    [Fact]
    public void BottomExceedsScale_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => new NormalizedRect(0, 0, 100, NormalizedRect.Scale + 1));
        Assert.Contains("Bottom", ex.Message);
    }

    [Fact]
    public void ZeroWidth_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new NormalizedRect(50, 0, 50, 100));
        Assert.Contains("Right", ex.Message);
    }

    [Fact]
    public void ZeroHeight_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new NormalizedRect(0, 50, 100, 50));
        Assert.Contains("Bottom", ex.Message);
    }

    [Fact]
    public void ReversedBounds_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new NormalizedRect(100, 0, 50, 100));
        Assert.Contains("Right", ex.Message);
    }

    // ═══ Width / Height ═══

    [Fact]
    public void Width_IsCorrect()
    {
        var r = new NormalizedRect(300, 100, 800, 600);
        Assert.Equal(500, r.Width);
    }

    [Fact]
    public void Height_IsCorrect()
    {
        var r = new NormalizedRect(300, 100, 800, 600);
        Assert.Equal(500, r.Height);
    }

    // ═══ Contains ═══

    [Fact]
    public void Contains_InsidePoint_ReturnsTrue()
    {
        var r = new NormalizedRect(1000, 2000, 5000, 6000);
        Assert.True(r.Contains(3000, 4000));
    }

    [Fact]
    public void Contains_AtOrigin_ReturnsTrue()
    {
        var r = new NormalizedRect(0, 0, 5000, 5000);
        Assert.True(r.Contains(0, 0));
    }

    [Fact]
    public void Contains_AtRightEdge_ReturnsFalse()
    {
        var r = new NormalizedRect(0, 0, 5000, 5000);
        Assert.False(r.Contains(5000, 2500));
    }

    [Fact]
    public void Contains_AtBottomEdge_ReturnsFalse()
    {
        var r = new NormalizedRect(0, 0, 5000, 5000);
        Assert.False(r.Contains(2500, 5000));
    }

    [Fact]
    public void Contains_OutsideLeft_ReturnsFalse()
    {
        var r = new NormalizedRect(1000, 1000, 5000, 5000);
        Assert.False(r.Contains(500, 2500));
    }

    // ═══ Overlaps ═══

    [Fact]
    public void Overlaps_AdjacentRects_ReturnsFalse()
    {
        var a = new NormalizedRect(0, 0, 5000, 5000);
        var b = new NormalizedRect(5000, 0, 10000, 5000);
        Assert.False(a.Overlaps(b));
        Assert.False(b.Overlaps(a));
    }

    [Fact]
    public void Overlaps_VerticallyAdjacent_ReturnsFalse()
    {
        var a = new NormalizedRect(0, 0, 5000, 5000);
        var b = new NormalizedRect(0, 5000, 5000, 10000);
        Assert.False(a.Overlaps(b));
    }

    [Fact]
    public void Overlaps_CrossingRects_ReturnsTrue()
    {
        var a = new NormalizedRect(0, 0, 6000, 6000);
        var b = new NormalizedRect(4000, 4000, 10000, 10000);
        Assert.True(a.Overlaps(b));
        Assert.True(b.Overlaps(a));
    }

    [Fact]
    public void Overlaps_FullyContained_ReturnsTrue()
    {
        var outer = new NormalizedRect(1000, 1000, 9000, 9000);
        var inner = new NormalizedRect(3000, 3000, 7000, 7000);
        Assert.True(outer.Overlaps(inner));
        Assert.True(inner.Overlaps(outer));
    }

    [Fact]
    public void Overlaps_Disjoint_ReturnsFalse()
    {
        var a = new NormalizedRect(0, 0, 3000, 3000);
        var b = new NormalizedRect(7000, 7000, 10000, 10000);
        Assert.False(a.Overlaps(b));
    }

    // ═══ FullyContains ═══

    [Fact]
    public void FullyContains_Contained_ReturnsTrue()
    {
        var outer = new NormalizedRect(1000, 1000, 9000, 9000);
        var inner = new NormalizedRect(3000, 3000, 7000, 7000);
        Assert.True(outer.FullyContains(inner));
    }

    [Fact]
    public void FullyContains_PartiallyOutside_ReturnsFalse()
    {
        var outer = new NormalizedRect(1000, 1000, 5000, 5000);
        var inner = new NormalizedRect(4000, 4000, 7000, 7000);
        Assert.False(outer.FullyContains(inner));
    }

    // ═══ null 拒绝 ═══

    [Fact]
    public void Overlaps_NullOther_Throws()
    {
        var r = new NormalizedRect(0, 0, 5000, 5000);
        Assert.Throws<ArgumentNullException>(() => r.Overlaps(null!));
    }

    [Fact]
    public void FullyContains_NullOther_Throws()
    {
        var r = new NormalizedRect(0, 0, 5000, 5000);
        Assert.Throws<ArgumentNullException>(() => r.FullyContains(null!));
    }

    // ═══ 引用值相等性 ═══

    [Fact]
    public void SameCoordinates_AreEqual()
    {
        var a = new NormalizedRect(1000, 2000, 5000, 6000);
        var b = new NormalizedRect(1000, 2000, 5000, 6000);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a == b);
    }

    [Fact]
    public void DifferentCoordinates_AreNotEqual()
    {
        var a = new NormalizedRect(1000, 2000, 5000, 6000);
        var b = new NormalizedRect(1000, 2000, 5001, 6000);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DefaultNormalizedRect_IsNull()
    {
        NormalizedRect? d = default;
        Assert.Null(d);
    }
}
