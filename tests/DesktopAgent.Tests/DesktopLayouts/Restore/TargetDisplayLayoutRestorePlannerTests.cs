using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

namespace DesktopAgent.Tests.DesktopLayouts.Restore;

public sealed class TargetDisplayLayoutRestorePlannerTests
{
    private static readonly PixelPoint DefaultSpacing = new(80, 80);
    private static readonly string TargetDevice = @"\\.\DISPLAY1";
    private static readonly PixelRect WorkArea = new(0, 0, 800, 600);
    private static int _snapOrdinal;

    public TargetDisplayLayoutRestorePlannerTests() { _snapOrdinal = 0; }

    private static DisplayDescriptor MakeDisplay(string name, PixelRect? workArea = null) =>
        new(name, new PixelRect(0, 0, 1920, 1080), workArea ?? WorkArea, 96, 96, name == TargetDevice);

    private static DesktopIconSnapshotItem SnapItem(string sid, PixelPoint pos, string? monitor = null) =>
        new(_snapOrdinal++, sid, $"Name_{sid}", null, ItemKind.File, pos, monitor ?? TargetDevice, false, true);

    private static DesktopIconDescriptor LiveIcon(string sid, PixelPoint pos, string? monitor = null,
        bool isFileSys = true, bool isSys = false) =>
        new(sid, $"Name_{sid}", null, isSys ? ItemKind.SystemItem : ItemKind.File,
            pos, monitor ?? TargetDevice, isSys, isFileSys);

    private static DesktopLayoutSnapshot MakeSnapshot(IEnumerable<DesktopIconSnapshotItem> icons, string? tm = null) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, tm ?? TargetDevice, ConnectionMethod.FindWindowSW, 1, icons);

    private static DesktopLayoutSnapshot MakeSnapshotNullTarget(IEnumerable<DesktopIconSnapshotItem> icons) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, null, ConnectionMethod.FindWindowSW, 1, icons);

    private static DesktopIconReadResult MakeReadResult(IEnumerable<DesktopIconDescriptor> icons, int failed = 0) =>
        new(icons.ToList(), failed, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);

    private static DesktopIconPositioningCapabilities MakeCapabilities(bool autoArrange = false) =>
        new(autoArrange, DefaultSpacing, ConnectionMethod.FindWindowSW, null);

    // ═══ 递增批量测试辅助 ═══
    private TargetDisplayLayoutRestorePlan PlanWithIcons(
        DesktopIconSnapshotItem[] snaps, DesktopIconDescriptor[] lives)
        => TargetDisplayLayoutRestorePlanner.CreatePlan(
            MakeSnapshot(snaps), MakeReadResult(lives),
            new[] { MakeDisplay(TargetDevice) }, MakeCapabilities(), TargetDevice);

    // ═══ 基本 ═══

    [Fact] public void AllAlreadyAtTarget_IsNoOp()
    {
        var p = PlanWithIcons(
            [SnapItem("a", new(160, 240)), SnapItem("b", new(240, 320))],
            [LiveIcon("a", new(160, 240)), LiveIcon("b", new(240, 320))]);
        Assert.True(p.IsNoOp); Assert.Equal(2, p.AlreadyAtTargetCount); Assert.Equal(0, p.MoveRequiredCount);
    }

    [Fact] public void SingleMove()
    {
        var p = PlanWithIcons([SnapItem("a", new(240, 320))], [LiveIcon("a", new(160, 240))]);
        Assert.Equal(1, p.MoveRequiredCount); Assert.False(p.HasBlockingErrors);
        Assert.NotNull(p.MoveExecutionOrder); Assert.Single(p.MoveExecutionOrder);
    }

    [Fact] public void IncompleteRead_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TargetDisplayLayoutRestorePlanner.CreatePlan(
                MakeSnapshot([SnapItem("a", new(160, 240))]),
                MakeReadResult([LiveIcon("a", new(160, 240))], failed: 3),
                new[] { MakeDisplay(TargetDevice) }, MakeCapabilities(), TargetDevice));
        Assert.Contains("3", ex.Message);
    }

    [Fact] public void AutoArrange_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TargetDisplayLayoutRestorePlanner.CreatePlan(
                MakeSnapshot([SnapItem("a", new(160, 240))]),
                MakeReadResult([LiveIcon("a", new(160, 240))]),
                new[] { MakeDisplay(TargetDevice) }, MakeCapabilities(autoArrange: true), TargetDevice));
        Assert.Contains("自动排列", ex.Message);
    }

    [Fact] public void TargetDisplayNotFound_Throws() => Assert.Throws<InvalidOperationException>(() =>
        TargetDisplayLayoutRestorePlanner.CreatePlan(
            MakeSnapshot([SnapItem("a", new(160, 240))]),
            MakeReadResult([LiveIcon("a", new(160, 240))]),
            new[] { MakeDisplay(@"\\.\DISPLAY2") }, MakeCapabilities(), TargetDevice));

    [Fact] public void DuplicateTargetDisplay_Throws() => Assert.Throws<InvalidOperationException>(() =>
        TargetDisplayLayoutRestorePlanner.CreatePlan(
            MakeSnapshot([SnapItem("a", new(160, 240))]),
            MakeReadResult([LiveIcon("a", new(160, 240))]),
            new[] { MakeDisplay(TargetDevice), MakeDisplay(TargetDevice) },
            MakeCapabilities(), TargetDevice));

    [Fact] public void SnapshotNullMonitor_Throws() => Assert.Throws<InvalidOperationException>(() =>
        TargetDisplayLayoutRestorePlanner.CreatePlan(
            MakeSnapshotNullTarget([SnapItem("a", new(160, 240))]),
            MakeReadResult([LiveIcon("a", new(160, 240))]),
            new[] { MakeDisplay(TargetDevice) }, MakeCapabilities(), TargetDevice));

    [Fact] public void SnapshotMonitorMismatch_Throws() => Assert.Throws<InvalidOperationException>(() =>
        TargetDisplayLayoutRestorePlanner.CreatePlan(
            MakeSnapshot([SnapItem("a", new(160, 240))], tm: @"\\.\DISPLAY2"),
            MakeReadResult([LiveIcon("a", new(160, 240))]),
            new[] { MakeDisplay(TargetDevice) }, MakeCapabilities(), TargetDevice));

    [Fact] public void EmptyTargetSet_WithoutGlobalIssue_IsNoOp()
    {
        var p = TargetDisplayLayoutRestorePlanner.CreatePlan(
            MakeSnapshot([SnapItem("a", new(160, 240), monitor: @"\\.\DISPLAY2")]),
            MakeReadResult([LiveIcon("a", new(160, 240), monitor: @"\\.\DISPLAY2")]),
            new[] { MakeDisplay(TargetDevice), MakeDisplay(@"\\.\DISPLAY2") },
            MakeCapabilities(), TargetDevice);
        Assert.True(p.IsNoOp); Assert.Equal(0, p.SnapshotTargetIconCount);
        Assert.False(p.HasBlockingErrors); Assert.Empty(p.Issues);
    }

    [Fact] public void EmptyTargetSet_GlobalDuplicateCurrentStableId_Blocks()
    {
        var p = TargetDisplayLayoutRestorePlanner.CreatePlan(
            MakeSnapshot([SnapItem("a", new(160, 240), monitor: @"\\.\DISPLAY2")]),
            MakeReadResult([LiveIcon("x", new(160, 240)), LiveIcon("x", new(320, 400))]), // x 重复
            new[] { MakeDisplay(TargetDevice), MakeDisplay(@"\\.\DISPLAY2") }, MakeCapabilities(), TargetDevice);
        Assert.False(p.IsNoOp); Assert.True(p.HasBlockingErrors);
        Assert.Contains(p.Issues, iss => iss.Code == TargetDisplayLayoutRestorePlanIssueCode.DuplicateCurrentStableId);
    }

    [Fact] public void EmptyTargetSet_CurrentRectangleOverflow_Blocks()
    {
        var overIcon = new DesktopIconDescriptor("x", "Overflow", null, ItemKind.File,
            new PixelPoint(int.MaxValue - 1, 0), TargetDevice, false, true);
        var p = TargetDisplayLayoutRestorePlanner.CreatePlan(
            MakeSnapshot([SnapItem("a", new(160, 240), monitor: @"\\.\DISPLAY2")]),
            MakeReadResult([overIcon]),
            new[] { MakeDisplay(TargetDevice), MakeDisplay(@"\\.\DISPLAY2") }, MakeCapabilities(), TargetDevice);
        Assert.Contains(p.Issues, iss => iss.Code == TargetDisplayLayoutRestorePlanIssueCode.CurrentRectangleOverflow);
    }

    // ═══ 快照级多错误诊断 ═══

    [Fact] public void SnapshotItemMultipleErrors_PreservesAllSnapshotDiagnostics()
    {
        // 同一个 StableId 出现两次 + 其中一个与其他图标目标坐标重复
        var p = PlanWithIcons(
            [SnapItem("a", new(160, 240)), SnapItem("a", new(160, 240)),
             SnapItem("b", new(160, 240))],
            [LiveIcon("a", new(80, 240)), LiveIcon("b", new(400, 400))]);
        Assert.Equal(3, p.SnapshotTargetIconCount);
        Assert.Equal(3, p.Items.Count);
        var dupSidItems = p.Items.Where(i => i.State == TargetDisplayLayoutRestoreItemState.SnapshotStableIdDuplicate).ToList();
        var dupPosItems = p.Items.Where(i => i.State == TargetDisplayLayoutRestoreItemState.DuplicateSnapshotTargetPosition).ToList();
        Assert.Equal(2, dupSidItems.Count);  // "a" 重复的两个条目
        Assert.Single(dupPosItems);  // "b" 与 "a" 坐标重复
        // 每个 "a" 重复项必须同时包含 StableId 重复和坐标重复信息
        foreach (var item in dupSidItems)
        {
            Assert.Contains("出现 2 次", item.BlockReason); // StableId 重复
            Assert.Contains("不同 StableId", item.BlockReason); // 坐标重复
        }
        Assert.Contains("不同 StableId", dupPosItems[0].BlockReason); // b 的坐标重复
    }

    // ═══ 不变量 ═══

    [Fact] public void ItemsCount_EqualsSnapshotTargetIconCount()
    {
        var p = PlanWithIcons(
            [SnapItem("a", new(160, 240)), SnapItem("b", new(240, 320)), SnapItem("c", new(320, 400))],
            [LiveIcon("a", new(160, 240)), LiveIcon("b", new(240, 320)), LiveIcon("c", new(320, 400))]);
        Assert.Equal(p.SnapshotTargetIconCount, p.Items.Count);
        Assert.Equal(3, p.Items.Count);
    }

    // ═══ 快照 StableId 重复 ═══

    [Fact] public void SnapshotSidDuplicate_MarksEveryEntry()
    {
        var p = PlanWithIcons(
            [SnapItem("a", new(160, 240)), SnapItem("a", new(240, 320))],
            [LiveIcon("a", new(160, 240))]);
        Assert.Equal(2, p.SnapshotTargetIconCount);
        Assert.Equal(2, p.Items.Count);
        Assert.All(p.Items, i => Assert.Equal(TargetDisplayLayoutRestoreItemState.SnapshotStableIdDuplicate, i.State));
    }

    [Fact] public void SnapshotSidTripleDuplicate_MarksEveryEntry()
    {
        var p = PlanWithIcons(
            [SnapItem("a", new(160, 240)), SnapItem("a", new(240, 320)), SnapItem("a", new(320, 400))],
            [LiveIcon("a", new(160, 240))]);
        Assert.Equal(3, p.Items.Count);
        Assert.All(p.Items, i => Assert.Equal(TargetDisplayLayoutRestoreItemState.SnapshotStableIdDuplicate, i.State));
    }

    // ═══ 坐标重复 ═══

    [Fact] public void DuplicatePos_MarksBothEntries()
    {
        var p = PlanWithIcons(
            [SnapItem("a", new(160, 240)), SnapItem("b", new(160, 240))],
            [LiveIcon("a", new(80, 240)), LiveIcon("b", new(400, 400))]);
        Assert.Equal(2, p.Items.Count);
        var dupItems = p.Items.Where(i => i.State == TargetDisplayLayoutRestoreItemState.DuplicateSnapshotTargetPosition).ToList();
        Assert.Equal(2, dupItems.Count);
    }

    [Fact] public void DuplicatePos_DetectedWhenOneMissing()
    {
        var p = PlanWithIcons(
            [SnapItem("a", new(160, 240)), SnapItem("b", new(160, 240))],
            [LiveIcon("b", new(400, 400))]); // a 缺失
        Assert.Equal(2, p.Items.Count);
        Assert.Equal(2, p.Items.Count(i => i.State == TargetDisplayLayoutRestoreItemState.DuplicateSnapshotTargetPosition));
    }

    [Fact] public void DuplicatePos_NotOverwrittenByOccupancy()
    {
        var p = PlanWithIcons(
            [SnapItem("a", new(160, 240)), SnapItem("b", new(160, 240))],
            [LiveIcon("a", new(80, 240)), LiveIcon("b", new(80, 240))]); // b 占据 a 的目标附近
        Assert.True(p.Items.All(i =>
            i.State == TargetDisplayLayoutRestoreItemState.DuplicateSnapshotTargetPosition));
    }

    // ═══ 全局当前 StableId 重复 ═══

    [Fact] public void GlobalCurrentSidDuplicate_BlocksPlan()
    {
        var snaps = new[] { SnapItem("a", new(240, 320)) };
        var lives = new[] { LiveIcon("a", new(160, 240)), LiveIcon("a", new(320, 400)) };
        var p = TargetDisplayLayoutRestorePlanner.CreatePlan(
            MakeSnapshot(snaps), MakeReadResult(lives),
            new[] { MakeDisplay(TargetDevice) }, MakeCapabilities(), TargetDevice);
        Assert.True(p.HasBlockingErrors);
        Assert.Contains(p.Issues, iss => iss.Code == TargetDisplayLayoutRestorePlanIssueCode.DuplicateCurrentStableId);
    }

    [Fact] public void GlobalCurrentSidDuplicate_BlocksEvenWhenNotTargeted()
    {
        var p = PlanWithIcons(
            [SnapItem("a", new(160, 240))],
            [LiveIcon("a", new(160, 240)), LiveIcon("b", new(80, 160)), LiveIcon("b", new(240, 320))]);
        // b 重复但不在快照中 → 全局 Issue
        Assert.True(p.HasBlockingErrors);
        Assert.Contains(p.Issues, iss => iss.RelatedStableId == "b");
    }

    // ═══ 占用分析 ═══

    [Fact] public void Occupancy_SkipsOnlyMatchedCurrentIndex()
    {
        // a 当前(160,240) 目标(200,240); b 也位于(160,240)
        // a 的目标矩形(200,240)-(280,320) 与 b 矩形(160,240)-(240,320) 重叠
        // 排除 a 自身索引后，b 仍占用目标 → a 被阻断
        var p = PlanWithIcons(
            [SnapItem("a", new(200, 240))],
            [LiveIcon("a", new(160, 240)), LiveIcon("b", new(160, 240))]);
        var aItem = p.Items.First(i => i.StableId == "a");
        Assert.Equal(TargetDisplayLayoutRestoreItemState.BlockedByStationaryIcon, aItem.State);
        Assert.Contains("b", aItem.BlockReason);
        Assert.Contains("Name_b", aItem.BlockReason);
    }

    [Fact] public void AlreadyAtTargetOccupier_BlocksMove()
    {
        // b AlreadyAtTarget at (240,320), a 目标 (280,360) 与 b 矩形重叠但不能是同一坐标
        // b 矩形 (240,320)-(320,400), a 目标矩形 (280,360)-(360,440) → 重叠 → a 被阻断
        var p = PlanWithIcons(
            [SnapItem("a", new(280, 360)), SnapItem("b", new(240, 320))],
            [LiveIcon("a", new(160, 240)), LiveIcon("b", new(240, 320))]);
        Assert.True(p.HasBlockingErrors);
        var aItem = p.Items.First(i => i.StableId == "a");
        Assert.Equal(TargetDisplayLayoutRestoreItemState.BlockedByStationaryIcon, aItem.State);
        Assert.Contains("a", aItem.BlockReason);
        var bItem = p.Items.First(i => i.StableId == "b");
        Assert.Equal(TargetDisplayLayoutRestoreItemState.AlreadyAtTarget, bItem.State);
        Assert.Null(p.MoveExecutionOrder);
    }

    [Fact] public void MultipleOccupiers_IncludingAlreadyAtTarget_Blocks()
    {
        // A 当前(80,80) 目标(200,240)
        // B 当前(160,240) 目标(400,400) — MoveRequired，矩形与 A 目标重叠
        // C 当前(240,240) 目标(240,240) — AlreadyAtTarget，矩形与 A 目标重叠
        // A 目标(200,240) 矩形(200,240)-(280,320)
        // B 当前(160,240) 矩形(160,240)-(240,320) → 重叠 ✓
        // C 当前(240,240) 矩形(240,240)-(320,320) → 重叠 ✓
        var p = PlanWithIcons(
            [SnapItem("a", new(200, 240)), SnapItem("b", new(400, 400)), SnapItem("c", new(240, 240))],
            [LiveIcon("a", new(80, 80)), LiveIcon("b", new(160, 240)), LiveIcon("c", new(240, 240))]);
        Assert.True(p.HasBlockingErrors);
        Assert.Null(p.MoveExecutionOrder);
        var aItem = p.Items.First(i => i.StableId == "a");
        Assert.Equal(TargetDisplayLayoutRestoreItemState.BlockedByStationaryIcon, aItem.State);
        Assert.Contains("a", aItem.BlockReason);
        Assert.Contains("(200,240)", aItem.BlockReason);
        Assert.Contains("c", aItem.BlockReason);
        Assert.Contains("Name_c", aItem.BlockReason);
        Assert.Contains("已就位", aItem.BlockReason);
        // b 是 MoveRequired，不进入静态阻断诊断
        var bItem = p.Items.First(i => i.StableId == "b");
        Assert.Equal(TargetDisplayLayoutRestoreItemState.MoveRequired, bItem.State);
        var cItem = p.Items.First(i => i.StableId == "c");
        Assert.Equal(TargetDisplayLayoutRestoreItemState.AlreadyAtTarget, cItem.State);
        Assert.DoesNotContain(p.Dependencies, d => d.ActionItem == "a");
    }

    [Fact] public void IconOnOtherDisplay_SamePixelPosition_StillMoveRequired()
    {
        // a 当前位置和快照目标相同，但在另一个显示器 → 应标记 MoveRequired
        var p = TargetDisplayLayoutRestorePlanner.CreatePlan(
            MakeSnapshot([SnapItem("a", new(160, 240))]),
            MakeReadResult([LiveIcon("a", new(160, 240), monitor: @"\\.\DISPLAY2")]),
            new[] { MakeDisplay(TargetDevice), MakeDisplay(@"\\.\DISPLAY2") },
            MakeCapabilities(), TargetDevice);
        Assert.Contains(p.Items, i => i.State == TargetDisplayLayoutRestoreItemState.MoveRequired && i.StableId == "a");
    }

    [Fact] public void Occupancy_MultipleOccupiers_StationaryBlocks()
    {
        var p = PlanWithIcons(
            [SnapItem("a", new(240, 320))],
            [LiveIcon("a", new(160, 240)), LiveIcon("x", new(240, 310)), LiveIcon("y", new(230, 320))]);
        Assert.Contains(p.Items, i => i.State == TargetDisplayLayoutRestoreItemState.BlockedByStationaryIcon);
    }

    // ═══ 阻断固定点传播 ═══

    [Fact] public void BlockedOccupier_PropagatesToDependent()
    {
        var p = PlanWithIcons(
            [SnapItem("a", new(240, 320)), SnapItem("b", new(400, 480))],
            [LiveIcon("a", new(160, 240)), LiveIcon("b", new(240, 320)), // b 占据 a 的目标
             LiveIcon("x", new(400, 480))]); // x 占据 b 的目标（非计划图标）
        // b → BlockedByStationaryIcon → a → BlockedByStationaryIcon（传播）
        var aItem = p.Items.First(i => i.StableId == "a");
        var bItem = p.Items.First(i => i.StableId == "b");
        Assert.Equal(TargetDisplayLayoutRestoreItemState.BlockedByStationaryIcon, bItem.State);
        Assert.Equal(TargetDisplayLayoutRestoreItemState.BlockedByStationaryIcon, aItem.State);
    }

    [Fact] public void BlockedChain_PropagatesUntilFixedPoint()
    {
        var p = PlanWithIcons(
            [SnapItem("a", new(240, 320)), SnapItem("b", new(320, 400)), SnapItem("c", new(400, 480))],
            [LiveIcon("a", new(160, 240)), LiveIcon("b", new(240, 320)), LiveIcon("c", new(320, 400)),
             LiveIcon("x", new(400, 480))]);
        Assert.All(p.Items, i => Assert.Equal(TargetDisplayLayoutRestoreItemState.BlockedByStationaryIcon, i.State));
    }

    // ═══ 依赖 ═══

    [Fact] public void Dependencies_ContainOnlyFinalMoveRequired()
    {
        // 交换场景：两个图标形成环，依赖存在于 SCC 中
        var p = PlanWithIcons(
            [SnapItem("a", new(240, 320)), SnapItem("b", new(160, 240))],
            [LiveIcon("a", new(160, 240)), LiveIcon("b", new(240, 320))]);
        Assert.NotEmpty(p.Cycles); // 环在 SCC 中检测到
        Assert.True(p.RequiresTemporaryStaging);
        Assert.Null(p.MoveExecutionOrder);
    }

    // ═══ 环 ═══

    [Fact] public void TwoIcons_Cycle_RequiresStaging()
    {
        var p = PlanWithIcons(
            [SnapItem("a", new(240, 320)), SnapItem("b", new(160, 240))],
            [LiveIcon("a", new(160, 240)), LiveIcon("b", new(240, 320))]);
        Assert.True(p.RequiresTemporaryStaging);
        Assert.Single(p.Cycles);
        Assert.Equal(new[] { "a", "b" }, p.Cycles[0].StableIds);
        Assert.Null(p.MoveExecutionOrder);
    }

    [Fact] public void ThreeIcons_Cycle()
    {
        var p = PlanWithIcons(
            [SnapItem("a", new(240, 320)), SnapItem("b", new(320, 400)), SnapItem("c", new(160, 240))],
            [LiveIcon("a", new(160, 240)), LiveIcon("b", new(240, 320)), LiveIcon("c", new(320, 400))]);
        Assert.True(p.RequiresTemporaryStaging);
        Assert.Single(p.Cycles);
        Assert.Equal(new[] { "a", "b", "c" }, p.Cycles[0].StableIds);
    }

    [Fact] public void CycleWithDownstreamNode_CycleExcludesDownstreamNode()
    {
        // a↔b 环 + c 依赖 b（下游不进入环）
        // a 当前(160,240) 目标(320,400) → b 占用 → b→a
        // b 当前(320,400) 目标(160,240) → a 占用 → a→b (环 {a,b})
        // c 当前(80,240)  目标(360,440) → 与 b 矩形重叠 → b→c (链)
        var p = PlanWithIcons(
            [SnapItem("a", new(320, 400)), SnapItem("b", new(160, 240)), SnapItem("c", new(360, 440))],
            [LiveIcon("a", new(160, 240)), LiveIcon("b", new(320, 400)), LiveIcon("c", new(80, 240))]);
        Assert.True(p.RequiresTemporaryStaging);
        Assert.Single(p.Cycles);
        Assert.Equal(new[] { "a", "b" }, p.Cycles[0].StableIds);
        Assert.Null(p.MoveExecutionOrder);
    }

    [Fact] public void TwoDisjointCycles_ProducesTwoCycles()
    {
        // 两组独立交换，矩形完全不重叠
        // 环 1: a(80,80)↔b(160,80)  环 2: c(80,320)↔d(160,320)
        var p = PlanWithIcons(
            [SnapItem("a", new(160, 80)), SnapItem("b", new(80, 80)),
             SnapItem("c", new(160, 320)), SnapItem("d", new(80, 320))],
            [LiveIcon("a", new(80, 80)), LiveIcon("b", new(160, 80)),
             LiveIcon("c", new(80, 320)), LiveIcon("d", new(160, 320))]);
        Assert.True(p.RequiresTemporaryStaging);
        Assert.False(p.HasBlockingErrors);
        Assert.Null(p.MoveExecutionOrder);
        Assert.Equal(2, p.Cycles.Count);
        Assert.Contains(p.Cycles, c => c.StableIds.SequenceEqual(new[] { "a", "b" }));
        Assert.Contains(p.Cycles, c => c.StableIds.SequenceEqual(new[] { "c", "d" }));
    }

    [Fact] public void TwoDisjointCycles_OrderIsStable()
    {
        var p1 = PlanWithIcons(
            [SnapItem("a", new(160, 80)), SnapItem("b", new(80, 80)),
             SnapItem("c", new(160, 320)), SnapItem("d", new(80, 320))],
            [LiveIcon("a", new(80, 80)), LiveIcon("b", new(160, 80)),
             LiveIcon("c", new(80, 320)), LiveIcon("d", new(160, 320))]);
        var p2 = PlanWithIcons(
            [SnapItem("a", new(160, 80)), SnapItem("b", new(80, 80)),
             SnapItem("c", new(160, 320)), SnapItem("d", new(80, 320))],
            [LiveIcon("a", new(80, 80)), LiveIcon("b", new(160, 80)),
             LiveIcon("c", new(80, 320)), LiveIcon("d", new(160, 320))]);
        Assert.Equal(p1.Cycles.Count, p2.Cycles.Count);
        for (var i = 0; i < p1.Cycles.Count; i++)
            Assert.Equal(p1.Cycles[i].StableIds, p2.Cycles[i].StableIds);
    }

    [Fact] public void CycleOrdering_IsStable()
    {
        var p1 = PlanWithIcons(
            [SnapItem("a", new(240, 320)), SnapItem("b", new(160, 240))],
            [LiveIcon("a", new(160, 240)), LiveIcon("b", new(240, 320))]);
        var p2 = PlanWithIcons(
            [SnapItem("a", new(240, 320)), SnapItem("b", new(160, 240))],
            [LiveIcon("a", new(160, 240)), LiveIcon("b", new(240, 320))]);
        Assert.Equal(p1.Cycles.Count, p2.Cycles.Count);
        Assert.Equal(p1.Cycles[0].StableIds, p2.Cycles[0].StableIds);
    }

    // ═══ 依赖链 ═══

    [Fact] public void TwoIcons_ChainDependency()
    {
        var p = PlanWithIcons(
            [SnapItem("a", new(240, 320)), SnapItem("b", new(320, 400))],
            [LiveIcon("a", new(160, 240)), LiveIcon("b", new(240, 320))]);
        Assert.Equal(2, p.MoveRequiredCount);
        Assert.Single(p.Dependencies);
        Assert.Equal("b", p.Dependencies[0].DependsOn);
        Assert.Equal("a", p.Dependencies[0].ActionItem);
        Assert.NotNull(p.MoveExecutionOrder);
        Assert.Equal("b", p.MoveExecutionOrder![0]);
        Assert.Equal("a", p.MoveExecutionOrder![1]);
    }

    [Fact] public void ThreeIcons_ChainDependency()
    {
        var p = PlanWithIcons(
            [SnapItem("a", new(240, 320)), SnapItem("b", new(320, 400)), SnapItem("c", new(400, 480))],
            [LiveIcon("a", new(100, 100)), LiveIcon("b", new(240, 320)), LiveIcon("c", new(320, 400))]);
        Assert.Equal(3, p.MoveRequiredCount);
        Assert.Equal(2, p.Dependencies.Count);
        Assert.Equal("c", p.MoveExecutionOrder![0]);
        Assert.Equal("b", p.MoveExecutionOrder![1]);
        Assert.Equal("a", p.MoveExecutionOrder![2]);
    }

    // ═══ Ordinal 大小写 ═══

    [Fact] public void StableId_OrdinalCase()
    {
        var p = PlanWithIcons([SnapItem("SID", new(240, 320))], [LiveIcon("sid", new(160, 240))]);
        Assert.Equal(1, p.MissingCount);
    }

    [Fact] public void DeviceName_OrdinalCaseMismatch_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TargetDisplayLayoutRestorePlanner.CreatePlan(
                MakeSnapshot([SnapItem("a", new(160, 240))]),
                MakeReadResult([LiveIcon("a", new(160, 240))]),
                new[] { MakeDisplay(TargetDevice) }, MakeCapabilities(),
                @"\\.\display1")); // 大小写不同
        Assert.Contains("0 个匹配", ex.Message);
    }

    // ═══ 溢出 ═══

    [Fact] public void TargetRectOverflow_BecomesBlockedItem()
    {
        var snaps = new[] { new DesktopIconSnapshotItem(
            0, "a", "Name_a", null, ItemKind.File,
            new PixelPoint(int.MaxValue - 1, 0), TargetDevice, false, true) };
        var lives = new[] { LiveIcon("a", new(100, 100)) };
        var p = TargetDisplayLayoutRestorePlanner.CreatePlan(
            MakeSnapshot(snaps), MakeReadResult(lives),
            new[] { MakeDisplay(TargetDevice) }, MakeCapabilities(), TargetDevice);
        Assert.Contains(p.Items, i => i.State == TargetDisplayLayoutRestoreItemState.TargetOutsideCurrentWorkArea);
    }

    [Fact] public void CurrentRectOverflow_BecomesIssue()
    {
        var snaps = new[] { SnapItem("a", new(160, 240)) };
        var lives = new[] { LiveIcon("a", new(100, 100)),
            new DesktopIconDescriptor("extra", "Extra", null, ItemKind.File,
                new PixelPoint(int.MaxValue - 1, 0), TargetDevice, false, true) };
        var p = TargetDisplayLayoutRestorePlanner.CreatePlan(
            MakeSnapshot(snaps), MakeReadResult(lives),
            new[] { MakeDisplay(TargetDevice) }, MakeCapabilities(), TargetDevice);
        Assert.Contains(p.Issues, iss => iss.Code == TargetDisplayLayoutRestorePlanIssueCode.CurrentRectangleOverflow);
    }

    // ═══ 拓扑排序稳定性 ═══

    [Fact] public void TopologicalOrder_Stable()
    {
        var p1 = PlanWithIcons(
            [SnapItem("a", new(240, 320)), SnapItem("b", new(240, 240)), SnapItem("c", new(160, 240))],
            [LiveIcon("a", new(160, 240)), LiveIcon("b", new(80, 160)), LiveIcon("c", new(320, 400))]);
        var p2 = PlanWithIcons(
            [SnapItem("a", new(240, 320)), SnapItem("b", new(240, 240)), SnapItem("c", new(160, 240))],
            [LiveIcon("a", new(160, 240)), LiveIcon("b", new(80, 160)), LiveIcon("c", new(320, 400))]);
        Assert.Equal(p1.MoveExecutionOrder, p2.MoveExecutionOrder);
    }

    // ═══ 跨显示器 ═══

    [Fact] public void IconOnOtherDisplay_PlannedBack()
    {
        var p = TargetDisplayLayoutRestorePlanner.CreatePlan(
            MakeSnapshot([SnapItem("a", new(160, 240))]),
            MakeReadResult([LiveIcon("a", new(999, 999), monitor: @"\\.\DISPLAY2")]),
            new[] { MakeDisplay(TargetDevice), MakeDisplay(@"\\.\DISPLAY2") },
            MakeCapabilities(), TargetDevice);
        Assert.Equal(1, p.MoveRequiredCount);
    }

    [Fact] public void OtherDisplayIcons_Excluded()
    {
        var p = TargetDisplayLayoutRestorePlanner.CreatePlan(
            MakeSnapshot([SnapItem("a", new(160, 240), monitor: TargetDevice),
                SnapItem("b", new(300, 400), monitor: @"\\.\DISPLAY2")]),
            MakeReadResult([LiveIcon("a", new(160, 240))]),
            new[] { MakeDisplay(TargetDevice), MakeDisplay(@"\\.\DISPLAY2") },
            MakeCapabilities(), TargetDevice);
        Assert.Equal(1, p.SnapshotTargetIconCount);
        Assert.Contains(p.Items, i => i.StableId == "a");
        Assert.DoesNotContain(p.Items, i => i.StableId == "b");
    }

    // ═══ 状态分类 ═══

    [Fact] public void Missing() { var p = PlanWithIcons([SnapItem("a", new(160, 240))], []); Assert.Equal(1, p.MissingCount); }
    [Fact] public void NotFileSystem_Blocked()
    {
        var p = PlanWithIcons([SnapItem("a", new(240, 320))],
            [LiveIcon("a", new(160, 240), isFileSys: false, isSys: true)]);
        Assert.Contains(p.Items, i => i.State == TargetDisplayLayoutRestoreItemState.NotFileSystemItem);
    }
    [Fact] public void SystemItem_Blocked()
    {
        var p = PlanWithIcons([SnapItem("a", new(240, 320))],
            [LiveIcon("a", new(160, 240), isFileSys: true, isSys: true)]);
        Assert.Contains(p.Items, i => i.State == TargetDisplayLayoutRestoreItemState.SystemItem);
    }
    [Fact] public void NoMonitor_Blocked()
    {
        var icon = new DesktopIconDescriptor("a", "Name_a", null, ItemKind.File,
            new PixelPoint(160, 240), null, false, true);
        var p = PlanWithIcons([SnapItem("a", new(240, 320))], [icon]);
        Assert.Contains(p.Items, i => i.State == TargetDisplayLayoutRestoreItemState.NoMonitorDeviceName);
    }
    [Fact] public void OutsideWorkArea_Blocked()
    {
        var p = PlanWithIcons([SnapItem("a", new(900, 700))], [LiveIcon("a", new(160, 240))]);
        Assert.Contains(p.Items, i => i.State == TargetDisplayLayoutRestoreItemState.TargetOutsideCurrentWorkArea);
    }
    [Fact] public void SupportsNegativeCoords()
    {
        var display = new DisplayDescriptor(TargetDevice, new PixelRect(-800, -400, 0, 0),
            new PixelRect(-800, -400, 0, 0), 96, 96, true);
        var p = TargetDisplayLayoutRestorePlanner.CreatePlan(
            MakeSnapshot([SnapItem("a", new(-400, -200))]),
            MakeReadResult([LiveIcon("a", new(-320, -200))]),
            new[] { display }, MakeCapabilities(), TargetDevice);
        Assert.Equal(1, p.MoveRequiredCount);
    }

    // ═══ 参数验证 ═══
    [Fact] public void NullSnapshot_Throws() => Assert.Throws<ArgumentNullException>(() =>
        TargetDisplayLayoutRestorePlanner.CreatePlan(null!, MakeReadResult([]),
            new[] { MakeDisplay(TargetDevice) }, MakeCapabilities(), TargetDevice));
    [Fact] public void NullRead_Throws() => Assert.Throws<ArgumentNullException>(() =>
        TargetDisplayLayoutRestorePlanner.CreatePlan(MakeSnapshot([]), null!,
            new[] { MakeDisplay(TargetDevice) }, MakeCapabilities(), TargetDevice));
    [Fact] public void NullDisplays_Throws() => Assert.Throws<ArgumentNullException>(() =>
        TargetDisplayLayoutRestorePlanner.CreatePlan(MakeSnapshot([]), MakeReadResult([]),
            null!, MakeCapabilities(), TargetDevice));
    [Fact] public void NullCaps_Throws() => Assert.Throws<ArgumentNullException>(() =>
        TargetDisplayLayoutRestorePlanner.CreatePlan(MakeSnapshot([]), MakeReadResult([]),
            new[] { MakeDisplay(TargetDevice) }, null!, TargetDevice));
    [Fact] public void NullDevice_Throws() => Assert.Throws<ArgumentNullException>(() =>
        TargetDisplayLayoutRestorePlanner.CreatePlan(MakeSnapshot([]), MakeReadResult([]),
            new[] { MakeDisplay(TargetDevice) }, MakeCapabilities(), null!));
    [Fact] public void WhitespaceDevice_Throws() => Assert.Throws<ArgumentException>(() =>
        TargetDisplayLayoutRestorePlanner.CreatePlan(MakeSnapshot([]), MakeReadResult([]),
            new[] { MakeDisplay(TargetDevice) }, MakeCapabilities(), "   "));

    // ═══ M1-05C-B 实机修复：AlreadyAtTarget 边缘图标不阻断 ═══

    [Fact]
    public void AlreadyAtTargetNearWorkAreaEdge_IsNotBlocked()
    {
        // WorkArea=(0,0)-(800,600), Spacing=(80,80)
        // SnapTarget=(760,520), CurrentPos=(760,520)
        // 占用矩形=(760,520)-(840,600) 不完全位于工作区 — 但无需移动，不应阻断
        var p = PlanWithIcons(
            [SnapItem("a", new(760, 520))],
            [LiveIcon("a", new(760, 520))]);
        var item = Assert.Single(p.Items);
        Assert.Equal(TargetDisplayLayoutRestoreItemState.AlreadyAtTarget, item.State);
        Assert.Null(item.BlockReason);
        Assert.Equal(1, p.AlreadyAtTargetCount);
        Assert.Equal(0, p.BlockedCount);
        Assert.Equal(0, p.MoveRequiredCount);
        Assert.False(p.HasBlockingErrors);
        Assert.True(p.IsNoOp);
    }

    [Fact]
    public void MoveRequiredTargetNearWorkAreaEdge_RemainsBlocked()
    {
        // SnapTarget=(760,520), CurrentPos=(640,520) — 需要移动
        // 目标占用矩形(760,520)-(840,600)越出工作区(0,0)-(800,600)
        var p = PlanWithIcons(
            [SnapItem("a", new(760, 520))],
            [LiveIcon("a", new(640, 520))]);
        var item = Assert.Single(p.Items);
        Assert.Equal(TargetDisplayLayoutRestoreItemState.TargetOutsideCurrentWorkArea, item.State);
        Assert.NotNull(item.BlockReason);
        Assert.Contains("越出工作区", item.BlockReason);
        Assert.Equal(0, p.MoveRequiredCount);
        Assert.Equal(1, p.BlockedCount);
        Assert.True(p.HasBlockingErrors);
        Assert.Null(p.MoveExecutionOrder);
    }

    [Fact]
    public void SameCoordinatesOnOtherDisplay_DoesNotBypassValidation()
    {
        // SnapTarget=(760,520), CurrentPos=(760,520) 但 CurrentMonitor=DISPLAY2
        // 坐标相同但显示器不同 → AlreadyAtTarget 要求 Ordinal 显示器匹配 → 不适用
        // 目标占用矩形(760,520)-(840,600)越出 TargetDevice 工作区(0,0)-(800,600)
        // → TargetOutsideCurrentWorkArea
        var p = TargetDisplayLayoutRestorePlanner.CreatePlan(
            MakeSnapshot([SnapItem("a", new(760, 520))]),
            MakeReadResult([LiveIcon("a", new(760, 520), monitor: @"\\.\DISPLAY2")]),
            new[] { MakeDisplay(TargetDevice), MakeDisplay(@"\\.\DISPLAY2") },
            MakeCapabilities(), TargetDevice);
        var item = Assert.Single(p.Items);
        Assert.NotEqual(TargetDisplayLayoutRestoreItemState.AlreadyAtTarget, item.State);
        Assert.Equal(TargetDisplayLayoutRestoreItemState.TargetOutsideCurrentWorkArea, item.State);
        Assert.True(p.HasBlockingErrors);
    }

    [Fact]
    public void SingleMoveWithEdgeAlreadyAtTarget_ProducesExecutablePlan()
    {
        // 复现实机场景的最小纯逻辑版本：
        // a: Curr=(160,240) Target=(240,240) MoveRequired
        // b: Curr=(760,520) Target=(760,520) AlreadyAtTarget 但占用矩形越出工作区右边界
        var p = PlanWithIcons(
            [SnapItem("a", new(240, 240)), SnapItem("b", new(760, 520))],
            [LiveIcon("a", new(160, 240)), LiveIcon("b", new(760, 520))]);
        Assert.Equal(1, p.MoveRequiredCount);
        Assert.Equal(1, p.AlreadyAtTargetCount);
        Assert.Equal(0, p.BlockedCount);
        Assert.False(p.HasBlockingErrors);
        Assert.NotNull(p.MoveExecutionOrder);
        Assert.Single(p.MoveExecutionOrder);
        Assert.Equal("a", p.MoveExecutionOrder![0]);

        // 验证 ExecutionPlan
        var ep = TargetDisplayLayoutExecutionPlanner.CreatePlan(
            p, MakeReadResult([LiveIcon("a", new(160, 240)), LiveIcon("b", new(760, 520))]),
            new[] { MakeDisplay(TargetDevice) }, MakeCapabilities());
        Assert.True(ep.IsExecutable);
        Assert.False(ep.IsNoOp);
        Assert.False(ep.UsesTemporaryStaging);
        Assert.Single(ep.Steps);
        Assert.Equal("a", ep.Steps[0].StableId);

        // 验证 ScopeGate
        var gate = TargetDisplayLayoutExecutionScopeGate.EvaluateSingleDirectMove(p, ep);
        Assert.True(gate.IsAllowed);
    }
}
