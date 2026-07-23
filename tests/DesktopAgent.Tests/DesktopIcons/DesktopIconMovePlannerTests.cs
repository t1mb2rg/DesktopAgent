using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopIcons;

public sealed class DesktopIconMovePlannerTests
{
    private static readonly PixelPoint Spacing = new(80, 80);
    private static readonly PixelRect WorkArea = new(0, 0, 800, 600);
    private static readonly PixelPoint Origin = new(160, 240);

    [Fact]
    public void PlanAdjacentMove_RightFree_ChoosesRight()
    {
        var occupied = new HashSet<PixelPoint>
        {
            new(Origin.X - Spacing.X, Origin.Y),  // Left
            new(Origin.X, Origin.Y + Spacing.Y), // Down
            new(Origin.X, Origin.Y - Spacing.Y), // Up
        };

        var plan = DesktopIconMovePlanner.PlanAdjacentMove(Origin, WorkArea, Spacing, occupied);

        Assert.Equal("Right", plan.Direction);
        Assert.Equal(new PixelPoint(Origin.X + Spacing.X, Origin.Y), plan.TargetPosition);
    }

    [Fact]
    public void PlanAdjacentMove_RightOccupied_ChoosesLeft()
    {
        var occupied = new HashSet<PixelPoint>
        {
            new(Origin.X + Spacing.X, Origin.Y),  // Right occupied
            new(Origin.X, Origin.Y + Spacing.Y), // Down
            new(Origin.X, Origin.Y - Spacing.Y), // Up
        };

        var plan = DesktopIconMovePlanner.PlanAdjacentMove(Origin, WorkArea, Spacing, occupied);

        Assert.Equal("Left", plan.Direction);
    }

    [Fact]
    public void PlanAdjacentMove_RightAndLeftOccupied_ChoosesDown()
    {
        var occupied = new HashSet<PixelPoint>
        {
            new(Origin.X + Spacing.X, Origin.Y),  // Right
            new(Origin.X - Spacing.X, Origin.Y),  // Left
            new(Origin.X, Origin.Y - Spacing.Y), // Up
        };

        var plan = DesktopIconMovePlanner.PlanAdjacentMove(Origin, WorkArea, Spacing, occupied);

        Assert.Equal("Down", plan.Direction);
    }

    [Fact]
    public void PlanAdjacentMove_RightLeftDownOccupied_ChoosesUp()
    {
        var occupied = new HashSet<PixelPoint>
        {
            new(Origin.X + Spacing.X, Origin.Y),  // Right
            new(Origin.X - Spacing.X, Origin.Y),  // Left
            new(Origin.X, Origin.Y + Spacing.Y), // Down
        };

        var plan = DesktopIconMovePlanner.PlanAdjacentMove(Origin, WorkArea, Spacing, occupied);

        Assert.Equal("Up", plan.Direction);
    }

    [Fact]
    public void PlanAdjacentMove_CandidateOutOfBounds_Skips()
    {
        var edgePosition = new PixelPoint(0, 240);
        var occupied = new HashSet<PixelPoint>
        {
            new(edgePosition.X + Spacing.X, edgePosition.Y),  // Right occupied
            new(edgePosition.X, edgePosition.Y + Spacing.Y), // Down
        };
        // Right occupied → Left out of bounds → Down occupied → Up

        var plan = DesktopIconMovePlanner.PlanAdjacentMove(
            edgePosition, WorkArea, Spacing, occupied);

        Assert.Equal("Up", plan.Direction);
    }

    [Fact]
    public void PlanAdjacentMove_SupportsNegativeCoordinates()
    {
        var negWorkArea = new PixelRect(-800, -400, 0, 0);
        var negOrigin = new PixelPoint(-400, -200);
        var spacing = new PixelPoint(100, 100);
        var occupied = new HashSet<PixelPoint>
        {
            new(negOrigin.X - spacing.X, negOrigin.Y),  // Left
            new(negOrigin.X, negOrigin.Y + spacing.Y), // Down
            new(negOrigin.X, negOrigin.Y - spacing.Y), // Up
        };

        var plan = DesktopIconMovePlanner.PlanAdjacentMove(
            negOrigin, negWorkArea, spacing, occupied);

        Assert.Equal("Right", plan.Direction);
        Assert.Equal(new PixelPoint(negOrigin.X + spacing.X, negOrigin.Y), plan.TargetPosition);
    }

    [Fact]
    public void PlanAdjacentMove_OverlapByRect_NotExactCoordinate_DetectsCollision()
    {
        // Right 候选 (240,240), 矩形 (240,240)-(320,320)
        // 占用 (250,250), 矩形 (250,250)-(330,330) 与 Right 重叠
        var origin = new PixelPoint(160, 240);
        var occupied = new HashSet<PixelPoint>
        {
            new PixelPoint(250, 250),  // 与 Right 候选矩形重叠
            new(origin.X, origin.Y + Spacing.Y), // Down occupied
            new(origin.X, origin.Y - Spacing.Y), // Up occupied
        };
        // Right → 重叠阻挡；Left 空闲

        var plan = DesktopIconMovePlanner.PlanAdjacentMove(
            origin, WorkArea, Spacing, occupied);

        Assert.Equal("Left", plan.Direction);
    }

    [Fact]
    public void PlanAdjacentMove_AllDirectionsUnavailable_Throws()
    {
        var origin = new PixelPoint(80, 80);
        var smallWorkArea = new PixelRect(0, 0, 160, 160);
        var occupied = new HashSet<PixelPoint>
        {
            new(origin.X + Spacing.X, origin.Y),  // Right
            new(origin.X - Spacing.X, origin.Y),  // Left — 超出 workArea
            new(origin.X, origin.Y + Spacing.Y), // Down
            new(origin.X, origin.Y - Spacing.Y), // Up — 超出 workArea
        };
        // Right 占用，Left 超出，Down 占用，Up 超出 → 全部不可用

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DesktopIconMovePlanner.PlanAdjacentMove(
                origin, smallWorkArea, Spacing, occupied));

        Assert.Contains("(80, 80)", ex.Message);
        Assert.Contains("(0, 0)", ex.Message);
        Assert.Contains("(160, 160)", ex.Message);
    }

    [Fact]
    public void PlanAdjacentMove_SpacingXZero_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            DesktopIconMovePlanner.PlanAdjacentMove(
                Origin, WorkArea, new PixelPoint(0, 80), []));
        Assert.Contains("spacing", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public void PlanAdjacentMove_SpacingYZero_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            DesktopIconMovePlanner.PlanAdjacentMove(
                Origin, WorkArea, new PixelPoint(80, 0), []));
        Assert.Contains("spacing", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public void PlanAdjacentMove_OriginalOutsideWorkArea_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            DesktopIconMovePlanner.PlanAdjacentMove(
                new PixelPoint(9999, 9999), WorkArea, Spacing, []));
        Assert.Contains("workarea", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public void PlanAdjacentMove_ZeroWorkArea_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            DesktopIconMovePlanner.PlanAdjacentMove(
                Origin, new PixelRect(0, 0, 0, 600), Spacing, []));
        Assert.Contains("workarea", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public void PlanAdjacentMove_OccupiedPositionsNull_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DesktopIconMovePlanner.PlanAdjacentMove(
                Origin, WorkArea, Spacing, null!));
    }

    [Fact]
    public void PlanAdjacentMove_Overflow_Throws()
    {
        // originalPosition.X + spacing.X 触发 checked 溢出
        // workArea 宽高有效，originalPosition 在 workArea 内
        var ex = Assert.Throws<OverflowException>(() =>
            DesktopIconMovePlanner.PlanAdjacentMove(
                new PixelPoint(int.MaxValue - 1, 0),
                new PixelRect(int.MaxValue - 2, -1, int.MaxValue, 1),
                new PixelPoint(2, 80),
                []));
        Assert.NotNull(ex);
    }
}
