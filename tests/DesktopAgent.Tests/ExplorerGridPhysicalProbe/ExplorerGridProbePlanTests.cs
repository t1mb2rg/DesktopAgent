using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.ExplorerGridPhysicalProbe;

namespace DesktopAgent.Tests.ExplorerGridPhysicalProbe;

public sealed class ExplorerGridProbePlanTests
{
    [Fact]
    public void Create_NegativeCoordinateDisplay_ProducesFullCrossAndFourSafeCorners()
    {
        var display = new DisplayDescriptor(
            "DISPLAY-LEFT",
            new PixelRect(-400, -200, 0, 300),
            new PixelRect(-400, -200, 0, 280),
            96,
            96,
            IsPrimary: false);

        var plan = ExplorerGridProbePlan.Create(
            display,
            new PixelPoint(100, 80),
            new PixelPoint(10, 20),
            new PixelPoint(-190, 20));

        Assert.Equal(new[] { -390, -290, -190 }, plan.Horizontal.Select(static slot => slot.Position.X));
        Assert.All(plan.Horizontal, slot => Assert.Equal(20, slot.Position.Y));
        Assert.Equal(new[] { -140, -60, 20, 100, 180 }, plan.Vertical.Select(static slot => slot.Position.Y));
        Assert.All(plan.Vertical, slot => Assert.Equal(-190, slot.Position.X));
        Assert.Equal(
            new[]
            {
                new PixelPoint(-390, -140),
                new PixelPoint(-190, -140),
                new PixelPoint(-390, 180),
                new PixelPoint(-190, 180),
            },
            plan.Corners.Select(static slot => slot.Position));
        Assert.All(
            plan.Horizontal.Concat(plan.Vertical).Concat(plan.Corners),
            slot => Assert.True(display.WorkArea.Contains(slot.Position)));
    }

    [Fact]
    public void Create_OccupiedSeedRowAndExactCorner_SelectsClearFullLinesAndEdgePoints()
    {
        var display = new DisplayDescriptor(
            "DISPLAY-1",
            new PixelRect(0, 0, 500, 500),
            new PixelRect(0, 0, 500, 500),
            96,
            96,
            IsPrimary: true);
        var spacing = new PixelPoint(100, 100);
        var occupied = new[]
        {
            new ExplorerGridProbeOccupant("left-1", new PixelPoint(10, 20)),
            new ExplorerGridProbeOccupant("left-2", new PixelPoint(10, 120)),
            new ExplorerGridProbeOccupant("left-3", new PixelPoint(10, 220)),
            new ExplorerGridProbeOccupant("left-4", new PixelPoint(10, 320)),
        };

        var plan = ExplorerGridProbePlan.Create(
            display,
            spacing,
            new PixelPoint(10, 20),
            new PixelPoint(210, 220),
            occupied);

        Assert.Equal(220, plan.Horizontal[0].Position.Y);
        Assert.Equal(4, plan.Horizontal.Count);
        Assert.Equal(4, plan.Vertical.Count);
        Assert.Equal(4, plan.Corners.Select(static slot => slot.Position).Distinct().Count());
        Assert.Equal("left-3", plan.Horizontal[0].OccupantStableId);
        Assert.Contains(plan.Corners, slot =>
            slot.Position == new PixelPoint(10, 20) && slot.OccupantStableId == "left-1");
        Assert.Equal(3, plan.Horizontal.Count(static slot => !slot.IsPassive));
        Assert.All(plan.Vertical, static slot => Assert.False(slot.IsPassive));
    }

    [Fact]
    public void Create_AllNativeSlotsOccupied_ReportsBlockedInsteadOfGeometryFailure()
    {
        var display = new DisplayDescriptor(
            "DISPLAY-FULL",
            new PixelRect(0, 0, 500, 500),
            new PixelRect(0, 0, 500, 500),
            96,
            96,
            IsPrimary: true);
        var occupants = Enumerable.Range(0, 4)
            .SelectMany(row => Enumerable.Range(0, 4)
                .Select(column => new ExplorerGridProbeOccupant(
                    $"occupied-{column}-{row}",
                    new PixelPoint(10 + column * 100, 20 + row * 100))))
            .ToArray();

        var exception = Assert.Throws<ExplorerGridProbeBlockedException>(() =>
            ExplorerGridProbePlan.Create(
                display,
                new PixelPoint(100, 100),
                new PixelPoint(10, 20),
                new PixelPoint(210, 220),
                occupants));

        Assert.Contains("主动落点", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_OffGridIconsThatPartiallyOverlapOneColumn_SelectsTheRemainingContiguousRun()
    {
        // 复刻真实机器（2026-10-13）\\.\DISPLAY1 的状态：WorkArea 左侧有 124px 任务栏条带，
        // 14 个图标停留在条带内（WorkArea 之外）的 x=90，另有 7 个图标停留在旧列 x=166；
        // 它们的 76×98 占用矩形部分压住第一列（x=154）与第二列（x=230）槽位。
        // 真机该屏还有 41 个精确在格上的图标（分散在 14 行），其中落在所选的 y=702 这一行上的
        // 有 4 个：只有它们会影响该行的 run，因此这里按真机补上，使该行的 active/passive 计数一致。
        var display = new DisplayDescriptor(
            "\\\\.\\DISPLAY1",
            new PixelRect(0, 0, 2560, 1440),
            new PixelRect(124, 0, 2560, 1440),
            96,
            96,
            IsPrimary: true);
        var spacing = new PixelPoint(76, 98);
        var seed = new PixelPoint(1446, 702);
        var onGridOnThisRow = new[] { 1218, 1294, 1370, 2054 }
            .Select(column => new ExplorerGridProbeOccupant($"grid-{column}", new PixelPoint(column, 702)));
        var occupants = Enumerable.Range(0, 14)
            .Select(row => new ExplorerGridProbeOccupant($"strip-{row}", new PixelPoint(90, 2 + row * 98)))
            .Concat(Enumerable.Range(0, 7)
                .Select(row => new ExplorerGridProbeOccupant($"legacy-{row}", new PixelPoint(166, 2 + row * 98))))
            .Concat(onGridOnThisRow)
            .ToArray();

        var plan = ExplorerGridProbePlan.Create(
            display,
            spacing,
            new PixelPoint(2, 16),
            seed,
            occupants);

        // 旧规则要求"整行 31 个槽位全部可分类"，于是 14 行全部作废 → Blocked。
        // 放宽后取最长连续可分类区间：x=154 被条带图标压住，x=230 在前 7 行还被旧列压住；
        // 最接近 seed 的行 y=702 只有 x=154 不可分类，因此从 x=230 起连续 30 个槽位可用。
        Assert.Equal(702, plan.Horizontal[0].Position.Y);
        Assert.Equal(new PixelPoint(230, 702), plan.Horizontal[0].Position);
        Assert.Equal(new PixelPoint(2434, 702), plan.Horizontal[^1].Position);
        Assert.Equal(30, plan.Horizontal.Count);
        Assert.DoesNotContain(plan.Horizontal, static slot => slot.Position.X == 154);
        // 真机这一行是 26 主动 + 4 被动；被动样本是"精确占位"证据，不写入 Measured 事实的样本数。
        Assert.Equal(26, plan.Horizontal.Count(static slot => !slot.IsPassive));
        Assert.Equal(4, plan.Horizontal.Count(static slot => slot.IsPassive));
        Assert.All(
            plan.Horizontal.Where(static slot => slot.IsPassive),
            slot => Assert.Contains(slot.Position.X, new[] { 1218, 1294, 1370, 2054 }));
    }

    [Fact]
    public void Create_NoRunWithEnoughActiveSlots_ReportsActionableBlockedDiagnostics()
    {
        var display = new DisplayDescriptor(
            "DISPLAY-STRIP",
            new PixelRect(0, 0, 600, 600),
            new PixelRect(100, 0, 600, 600),
            96,
            96,
            IsPrimary: true);
        var occupants = Enumerable.Range(0, 5)
            .SelectMany(row => new[]
            {
                new ExplorerGridProbeOccupant($"outside-{row}", new PixelPoint(40, 20 + row * 100)),
                new ExplorerGridProbeOccupant($"inside-{row}", new PixelPoint(140, 20 + row * 100)),
            })
            .ToArray();

        var exception = Assert.Throws<ExplorerGridProbeBlockedException>(() =>
            ExplorerGridProbePlan.Create(
                display,
                new PixelPoint(100, 100),
                new PixelPoint(10, 20),
                new PixelPoint(310, 220),
                occupants));

        Assert.Contains("主动落点", exception.Message, StringComparison.Ordinal);
        Assert.Contains("共检查 5 条横向扫描线，没有一条满足", exception.Message, StringComparison.Ordinal);
        Assert.Contains("最长连续可分类区间 2 个槽位", exception.Message, StringComparison.Ordinal);
        Assert.Contains("(110,220) 被 2 个占用者部分覆盖", exception.Message, StringComparison.Ordinal);
        Assert.Contains("(210,220)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("WorkArea 之外", exception.Message, StringComparison.Ordinal);
        Assert.Contains("将图标与网格对齐", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RowWithSeveralClassifiableSpans_UsesTheRunWithTheMostActiveSlots()
    {
        var display = new DisplayDescriptor(
            "DISPLAY-SPLIT",
            new PixelRect(0, 0, 700, 700),
            new PixelRect(0, 0, 700, 700),
            96,
            96,
            IsPrimary: true);
        // (160,10) 的图标同时压住 x=110 与 x=210 两列，只在 y=10 这一行制造断裂：
        // 左侧只剩 x=10 一个可分类槽位，右侧是 x=310/410/510 三个，应取主动槽位更多的那一段。
        var occupants = new[] { new ExplorerGridProbeOccupant("split", new PixelPoint(160, 10)) };

        var plan = ExplorerGridProbePlan.Create(
            display,
            new PixelPoint(100, 100),
            new PixelPoint(10, 10),
            new PixelPoint(310, 10),
            occupants);

        Assert.Equal(10, plan.Horizontal[0].Position.Y);
        Assert.Equal(new[] { 310, 410, 510 }, plan.Horizontal.Select(static slot => slot.Position.X));
        Assert.All(plan.Horizontal, static slot => Assert.False(slot.IsPassive));
    }
}
