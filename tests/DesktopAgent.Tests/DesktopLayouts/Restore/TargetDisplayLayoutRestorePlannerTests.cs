using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Restore;

public sealed class TargetDisplayLayoutRestorePlannerTests
{
    private static readonly PixelPoint DefaultSpacing = new(80, 80);
    private static readonly string TargetDevice = @"\\.\DISPLAY1";
    private static readonly PixelRect WorkArea = new(0, 0, 800, 600);
    private static int _snapOrdinal;

    private static DisplayDescriptor MakeDisplay(string name, PixelRect? workArea = null) =>
        new(name, new PixelRect(0, 0, 1920, 1080), workArea ?? WorkArea, 96, 96, name == TargetDevice);

    private static DesktopIconSnapshotItem SnapItem(
        string stableId, PixelPoint pos, string? monitor = null) =>
        new(_snapOrdinal++, stableId, $"Name_{stableId}", null, ItemKind.File, pos,
            monitor ?? TargetDevice, false, true);

    private static DesktopIconDescriptor LiveIcon(
        string stableId, PixelPoint pos, string? monitor = null, bool isFileSys = true, bool isSys = false) =>
        new(stableId, $"Name_{stableId}", null, isSys ? ItemKind.SystemItem : ItemKind.File,
            pos, monitor ?? TargetDevice, isSys, isFileSys);

    private static DesktopLayoutSnapshot MakeSnapshot(
        IEnumerable<DesktopIconSnapshotItem> icons, string? targetMonitor = null) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow,
            targetMonitor ?? TargetDevice, ConnectionMethod.FindWindowSW, 1, icons);

    private static DesktopLayoutSnapshot MakeSnapshotWithNullTarget(
        IEnumerable<DesktopIconSnapshotItem> icons) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow,
            null, ConnectionMethod.FindWindowSW, 1, icons);

    private static DesktopIconReadResult MakeReadResult(
        IEnumerable<DesktopIconDescriptor> icons, int failed = 0) =>
        new(icons.ToList(), failed, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null);

    public TargetDisplayLayoutRestorePlannerTests()
    {
        _snapOrdinal = 0;
    }

    private static DesktopIconPositioningCapabilities MakeCapabilities(bool autoArrange = false) =>
        new(autoArrange, DefaultSpacing, ConnectionMethod.FindWindowSW, null);

    // ═══ 1-2: 基本 IsNoOp ═══

    [Fact]
    public void AllIconsAlreadyAtTarget_IsNoOp()
    {
        var snap = MakeSnapshot([SnapItem("a", new(160, 240)), SnapItem("b", new(240, 320))]);
        var read = MakeReadResult([LiveIcon("a", new(160, 240)), LiveIcon("b", new(240, 320))]);
        var displays = new[] { MakeDisplay(TargetDevice) };

        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, displays, MakeCapabilities(), TargetDevice);

        Assert.True(plan.IsNoOp);
        Assert.Equal(2, plan.AlreadyAtTargetCount);
        Assert.Equal(0, plan.MoveRequiredCount);
        Assert.False(plan.HasBlockingErrors);
    }

    [Fact]
    public void SingleMove_TargetFree()
    {
        var snap = MakeSnapshot([SnapItem("a", new(240, 320))]);
        var read = MakeReadResult([LiveIcon("a", new(160, 240))]);
        var displays = new[] { MakeDisplay(TargetDevice) };

        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, displays, MakeCapabilities(), TargetDevice);

        Assert.Equal(1, plan.MoveRequiredCount);
        Assert.False(plan.HasBlockingErrors);
        Assert.False(plan.IsNoOp);
        Assert.NotNull(plan.MoveExecutionOrder);
        Assert.Single(plan.MoveExecutionOrder);
        Assert.Equal("a", plan.MoveExecutionOrder![0]);
    }

    // ═══ 3-4: 全局阻断 ═══

    [Fact]
    public void IncompleteRead_Throws()
    {
        var snap = MakeSnapshot([SnapItem("a", new(160, 240))]);
        var read = MakeReadResult([LiveIcon("a", new(160, 240))], failed: 3);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            TargetDisplayLayoutRestorePlanner.CreatePlan(
                snap, read, new[] { MakeDisplay(TargetDevice) }, MakeCapabilities(), TargetDevice));
        Assert.Contains("3", ex.Message);
    }

    [Fact]
    public void AutoArrangeEnabled_Throws()
    {
        var snap = MakeSnapshot([SnapItem("a", new(160, 240))]);
        var read = MakeReadResult([LiveIcon("a", new(160, 240))]);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            TargetDisplayLayoutRestorePlanner.CreatePlan(
                snap, read, new[] { MakeDisplay(TargetDevice) }, MakeCapabilities(autoArrange: true), TargetDevice));
        Assert.Contains("自动排列", ex.Message);
    }

    // ═══ 5-6: 目标显示器 ═══

    [Fact]
    public void TargetDisplayNotFound_Throws()
    {
        var snap = MakeSnapshot([SnapItem("a", new(160, 240))]);
        var read = MakeReadResult([LiveIcon("a", new(160, 240))]);

        Assert.Throws<InvalidOperationException>(() =>
            TargetDisplayLayoutRestorePlanner.CreatePlan(
                snap, read, new[] { MakeDisplay(@"\\.\DISPLAY2") }, MakeCapabilities(), TargetDevice));
    }

    [Fact]
    public void DuplicateTargetDisplay_Throws()
    {
        var snap = MakeSnapshot([SnapItem("a", new(160, 240))]);
        var read = MakeReadResult([LiveIcon("a", new(160, 240))]);
        var displays = new[] { MakeDisplay(TargetDevice), MakeDisplay(TargetDevice) };

        Assert.Throws<InvalidOperationException>(() =>
            TargetDisplayLayoutRestorePlanner.CreatePlan(
                snap, read, displays, MakeCapabilities(), TargetDevice));
    }

    // ═══ 7-8: 快照目标显示器验证 ═══

    [Fact]
    public void SnapshotTargetMonitorNull_Throws()
    {
        var snap = MakeSnapshotWithNullTarget([SnapItem("a", new(160, 240))]);
        var read = MakeReadResult([LiveIcon("a", new(160, 240))]);

        Assert.Throws<InvalidOperationException>(() =>
            TargetDisplayLayoutRestorePlanner.CreatePlan(
                snap, read, new[] { MakeDisplay(TargetDevice) }, MakeCapabilities(), TargetDevice));
    }

    [Fact]
    public void SnapshotTargetMonitorMismatch_Throws()
    {
        var snap = MakeSnapshot([SnapItem("a", new(160, 240))], targetMonitor: @"\\.\DISPLAY2");
        var read = MakeReadResult([LiveIcon("a", new(160, 240))]);

        Assert.Throws<InvalidOperationException>(() =>
            TargetDisplayLayoutRestorePlanner.CreatePlan(
                snap, read, new[] { MakeDisplay(TargetDevice) }, MakeCapabilities(), TargetDevice));
    }

    // ═══ 9: 目标集合为空 ═══

    [Fact]
    public void SnapshotTargetSetEmpty_IsNoOp()
    {
        // 快照有图标但不属于目标显示器
        var snap = MakeSnapshot([SnapItem("a", new(160, 240), monitor: @"\\.\DISPLAY2")]);
        var read = MakeReadResult([LiveIcon("a", new(160, 240))]);
        var displays = new[] { MakeDisplay(TargetDevice), MakeDisplay(@"\\.\DISPLAY2") };

        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, displays, MakeCapabilities(), TargetDevice);

        Assert.True(plan.IsNoOp);
        Assert.Equal(0, plan.SnapshotTargetIconCount);
    }

    // ═══ 10: 快照目标 StableId 重复 ═══

    [Fact]
    public void SnapshotStableIdDuplicate_Blocked()
    {
        var snap = MakeSnapshot([SnapItem("a", new(160, 240)), SnapItem("a", new(240, 320))]);
        var read = MakeReadResult([LiveIcon("a", new(160, 240))]);
        var displays = new[] { MakeDisplay(TargetDevice) };

        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, displays, MakeCapabilities(), TargetDevice);

        Assert.True(plan.HasBlockingErrors);
        Assert.Contains(plan.Items,
            i => i.State == TargetDisplayLayoutRestoreItemState.SnapshotStableIdDuplicate);
    }

    // ═══ 11: 当前 StableId 重复 ═══

    [Fact]
    public void CurrentStableIdDuplicate_Blocked()
    {
        var snap = MakeSnapshot([SnapItem("a", new(240, 320))]);
        var read = MakeReadResult([
            LiveIcon("a", new(160, 240)), LiveIcon("a", new(240, 320))
        ]);
        var displays = new[] { MakeDisplay(TargetDevice) };

        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, displays, MakeCapabilities(), TargetDevice);

        Assert.True(plan.HasBlockingErrors);
        Assert.Contains(plan.Items,
            i => i.State == TargetDisplayLayoutRestoreItemState.DuplicateCurrentStableId);
    }

    // ═══ 12: 缺失 ═══

    [Fact]
    public void MissingFromCurrent()
    {
        var snap = MakeSnapshot([SnapItem("a", new(160, 240))]);
        var read = MakeReadResult([LiveIcon("b", new(160, 240))]);
        var displays = new[] { MakeDisplay(TargetDevice) };

        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, displays, MakeCapabilities(), TargetDevice);

        Assert.Equal(1, plan.MissingCount);
        Assert.Contains(plan.Items,
            i => i.State == TargetDisplayLayoutRestoreItemState.MissingFromCurrent);
    }

    // ═══ 13-15: 可移动性 ═══

    [Fact]
    public void NotFileSystemItem_Blocked()
    {
        var snap = MakeSnapshot([SnapItem("a", new(240, 320))]);
        var read = MakeReadResult([LiveIcon("a", new(160, 240), isFileSys: false, isSys: true)]);
        var displays = new[] { MakeDisplay(TargetDevice) };

        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, displays, MakeCapabilities(), TargetDevice);

        Assert.Contains(plan.Items,
            i => i.State == TargetDisplayLayoutRestoreItemState.NotFileSystemItem);
    }

    [Fact]
    public void SystemItem_Blocked()
    {
        var snap = MakeSnapshot([SnapItem("a", new(240, 320))]);
        var read = MakeReadResult([LiveIcon("a", new(160, 240), isFileSys: true, isSys: true)]);
        var displays = new[] { MakeDisplay(TargetDevice) };

        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, displays, MakeCapabilities(), TargetDevice);

        Assert.Contains(plan.Items,
            i => i.State == TargetDisplayLayoutRestoreItemState.SystemItem);
    }

    [Fact]
    public void NoMonitorDeviceName_Blocked()
    {
        var snap = MakeSnapshot([SnapItem("a", new(240, 320))]);
        var icon = new DesktopIconDescriptor(
            "a", "Name_a", null, ItemKind.File,
            new PixelPoint(160, 240), null, false, true);
        var read = MakeReadResult([icon]);
        var displays = new[] { MakeDisplay(TargetDevice) };

        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, displays, MakeCapabilities(), TargetDevice);

        Assert.Contains(plan.Items,
            i => i.State == TargetDisplayLayoutRestoreItemState.NoMonitorDeviceName);
    }

    // ═══ 16-17: 目标边界 ═══

    [Fact]
    public void TargetOutsideWorkArea_Blocked()
    {
        var snap = MakeSnapshot([SnapItem("a", new(900, 700))]);
        var read = MakeReadResult([LiveIcon("a", new(160, 240))]);
        var displays = new[] { MakeDisplay(TargetDevice) };

        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, displays, MakeCapabilities(), TargetDevice);

        Assert.Contains(plan.Items,
            i => i.State == TargetDisplayLayoutRestoreItemState.TargetOutsideCurrentWorkArea);
    }

    [Fact]
    public void SupportsNegativeCoordinates()
    {
        var negWorkArea = new PixelRect(-800, -400, 0, 0);
        var display = new DisplayDescriptor(
            TargetDevice, new PixelRect(-800, -400, 0, 0),
            negWorkArea, 96, 96, true);
        var snap = MakeSnapshot([SnapItem("a", new(-400, -200))]);
        var read = MakeReadResult([LiveIcon("a", new(-320, -200))]);

        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, new[] { display }, MakeCapabilities(), TargetDevice);

        Assert.Equal(1, plan.MoveRequiredCount);
        Assert.False(plan.HasBlockingErrors);
    }

    // ═══ 18: 快照目标坐标重复 ═══

    [Fact]
    public void SnapshotTargetPositionDuplicate_Blocked()
    {
        var snap = MakeSnapshot([SnapItem("a", new(160, 240)), SnapItem("b", new(160, 240))]);
        var read = MakeReadResult([LiveIcon("a", new(80, 240)), LiveIcon("b", new(240, 320))]);
        var displays = new[] { MakeDisplay(TargetDevice) };

        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, displays, MakeCapabilities(), TargetDevice);

        Assert.True(plan.HasBlockingErrors);
        Assert.Contains(plan.Items,
            i => i.State == TargetDisplayLayoutRestoreItemState.DuplicateSnapshotTargetPosition);
    }

    // ═══ 19-20: 占用与依赖 ═══

    [Fact]
    public void TargetFree_NoDependency()
    {
        var snap = MakeSnapshot([SnapItem("a", new(240, 320))]);
        var read = MakeReadResult([LiveIcon("a", new(160, 240))]);
        var displays = new[] { MakeDisplay(TargetDevice) };

        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, displays, MakeCapabilities(), TargetDevice);

        Assert.Empty(plan.Dependencies);
    }

    [Fact]
    public void TargetBlockedByExtraIcon_StaticBlock()
    {
        var snap = MakeSnapshot([SnapItem("a", new(240, 320))]);
        var read = MakeReadResult([
            LiveIcon("a", new(160, 240)),
            LiveIcon("extra", new(240, 320)) // 占用目标位置的非计划图标
        ]);
        var displays = new[] { MakeDisplay(TargetDevice) };

        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, displays, MakeCapabilities(), TargetDevice);

        Assert.True(plan.HasBlockingErrors);
        Assert.Contains(plan.Items,
            i => i.State == TargetDisplayLayoutRestoreItemState.BlockedByStationaryIcon);
    }

    // ═══ 21-22: 依赖链 ═══

    [Fact]
    public void TwoIcons_ChainDependency()
    {
        var snap = MakeSnapshot([
            SnapItem("a", new(240, 320)), // a → (240,320) which is occupied by b
            SnapItem("b", new(320, 400)),
        ]);
        var read = MakeReadResult([
            LiveIcon("a", new(160, 240)),
            LiveIcon("b", new(240, 320)), // b occupies a's target
        ]);
        var displays = new[] { MakeDisplay(TargetDevice) };

        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, displays, MakeCapabilities(), TargetDevice);

        Assert.Equal(2, plan.MoveRequiredCount);
        Assert.NotEmpty(plan.Dependencies);
        Assert.NotNull(plan.MoveExecutionOrder);
    }

    [Fact]
    public void ThreeIcons_ChainDependency()
    {
        var snap = MakeSnapshot([
            SnapItem("a", new(240, 320)),
            SnapItem("b", new(320, 400)),
            SnapItem("c", new(400, 480)),
        ]);
        var read = MakeReadResult([
            LiveIcon("a", new(100, 100)),
            LiveIcon("b", new(240, 320)), // occupies a's target
            LiveIcon("c", new(320, 400)), // occupies b's target
        ]);
        var displays = new[] { MakeDisplay(TargetDevice) };

        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, displays, MakeCapabilities(), TargetDevice);

        Assert.Equal(3, plan.MoveRequiredCount);
        Assert.Equal(2, plan.Dependencies.Count);
        Assert.NotNull(plan.MoveExecutionOrder);
        // c must move first, then b, then a
        Assert.Equal("c", plan.MoveExecutionOrder![0]);
        Assert.Equal("b", plan.MoveExecutionOrder![1]);
        Assert.Equal("a", plan.MoveExecutionOrder![2]);
    }

    // ═══ 23-24: 环 ═══

    [Fact]
    public void TwoIcons_Cycle_RequiresStaging()
    {
        var snap = MakeSnapshot([
            SnapItem("a", new(240, 320)),
            SnapItem("b", new(160, 240)),
        ]);
        var read = MakeReadResult([
            LiveIcon("a", new(160, 240)), // a at b's target
            LiveIcon("b", new(240, 320)), // b at a's target
        ]);
        var displays = new[] { MakeDisplay(TargetDevice) };

        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, displays, MakeCapabilities(), TargetDevice);

        Assert.True(plan.RequiresTemporaryStaging);
        Assert.NotEmpty(plan.Cycles);
        Assert.Null(plan.MoveExecutionOrder);
    }

    [Fact]
    public void ThreeIcons_Cycle()
    {
        var snap = MakeSnapshot([
            SnapItem("a", new(240, 320)),
            SnapItem("b", new(320, 400)),
            SnapItem("c", new(160, 240)),
        ]);
        var read = MakeReadResult([
            LiveIcon("a", new(160, 240)), // a at c's target
            LiveIcon("b", new(240, 320)), // b at a's target
            LiveIcon("c", new(320, 400)), // c at b's target
        ]);
        var displays = new[] { MakeDisplay(TargetDevice) };

        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, displays, MakeCapabilities(), TargetDevice);

        Assert.True(plan.RequiresTemporaryStaging);
        Assert.NotEmpty(plan.Cycles);
    }

    // ═══ 25-26: Ordinal 大小写 ═══

    [Fact]
    public void StableId_OrdinalCase()
    {
        var snap = MakeSnapshot([SnapItem("SID", new(240, 320))]);
        var read = MakeReadResult([LiveIcon("sid", new(160, 240))]); // 大小写不同
        var displays = new[] { MakeDisplay(TargetDevice) };

        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, displays, MakeCapabilities(), TargetDevice);

        Assert.Equal(1, plan.MissingCount); // Ordinal 不匹配，视为缺失
    }

    [Fact]
    public void DeviceName_OrdinalCase()
    {
        var snap = MakeSnapshot([SnapItem("a", new(160, 240))]);
        var read = MakeReadResult([LiveIcon("a", new(160, 240))]);

        var ex = Assert.Throws<ArgumentException>(() =>
            TargetDisplayLayoutRestorePlanner.CreatePlan(
                snap, read, new[] { MakeDisplay(TargetDevice) }, MakeCapabilities(), ""));
    }

    // ═══ 27: 跨显示器移回 ═══

    [Fact]
    public void IconOnOtherDisplay_PlannedBackToTarget()
    {
        var otherDisplay = @"\\.\DISPLAY2";
        var snap = MakeSnapshot([SnapItem("a", new(160, 240))]); // 目标 DISPLAY1
        var read = MakeReadResult([
            LiveIcon("a", new(999, 999), monitor: otherDisplay) // 当前在 DISPLAY2
        ]);
        var displays = new[] { MakeDisplay(TargetDevice), MakeDisplay(otherDisplay) };

        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, displays, MakeCapabilities(), TargetDevice);

        Assert.Equal(1, plan.MoveRequiredCount);
        Assert.False(plan.HasBlockingErrors);
    }

    // ═══ 28: 其他显示器图标不参与 ═══

    [Fact]
    public void OtherDisplayIcons_Excluded()
    {
        var snap = MakeSnapshot([
            SnapItem("a", new(160, 240), monitor: TargetDevice),
            SnapItem("b", new(300, 400), monitor: @"\\.\DISPLAY2"),
        ]);
        var read = MakeReadResult([LiveIcon("a", new(160, 240))]);
        var displays = new[] { MakeDisplay(TargetDevice), MakeDisplay(@"\\.\DISPLAY2") };

        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, displays, MakeCapabilities(), TargetDevice);

        Assert.Equal(1, plan.SnapshotTargetIconCount);
        Assert.Contains(plan.Items, i => i.StableId == "a");
        Assert.DoesNotContain(plan.Items, i => i.StableId == "b");
    }

    // ═══ 29: 拓扑排序稳定 ═══

    [Fact]
    public void TopologicalOrder_Stable()
    {
        var snap = MakeSnapshot([
            SnapItem("a", new(240, 320)),
            SnapItem("b", new(240, 240)),
            SnapItem("c", new(160, 240)),
        ]);
        var read = MakeReadResult([
            LiveIcon("a", new(160, 240)),
            LiveIcon("b", new(80, 160)),
            LiveIcon("c", new(320, 400)),
        ]);
        var displays = new[] { MakeDisplay(TargetDevice) };

        var plan1 = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, displays, MakeCapabilities(), TargetDevice);
        var plan2 = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snap, read, displays, MakeCapabilities(), TargetDevice);

        Assert.Equal(plan1.MoveExecutionOrder, plan2.MoveExecutionOrder);
    }

    // ═══ 30: 参数验证 ═══

    [Fact]
    public void NullSnapshot_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            TargetDisplayLayoutRestorePlanner.CreatePlan(
                null!, MakeReadResult([]), new[] { MakeDisplay(TargetDevice) },
                MakeCapabilities(), TargetDevice));

    [Fact]
    public void NullReadResult_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            TargetDisplayLayoutRestorePlanner.CreatePlan(
                MakeSnapshot([]), null!, new[] { MakeDisplay(TargetDevice) },
                MakeCapabilities(), TargetDevice));

    [Fact]
    public void NullDisplays_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            TargetDisplayLayoutRestorePlanner.CreatePlan(
                MakeSnapshot([]), MakeReadResult([]), null!,
                MakeCapabilities(), TargetDevice));

    [Fact]
    public void NullCapabilities_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            TargetDisplayLayoutRestorePlanner.CreatePlan(
                MakeSnapshot([]), MakeReadResult([]), new[] { MakeDisplay(TargetDevice) },
                null!, TargetDevice));

    [Fact]
    public void NullTargetDeviceName_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            TargetDisplayLayoutRestorePlanner.CreatePlan(
                MakeSnapshot([]), MakeReadResult([]), new[] { MakeDisplay(TargetDevice) },
                MakeCapabilities(), null!));

    [Fact]
    public void WhitespaceTargetDeviceName_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            TargetDisplayLayoutRestorePlanner.CreatePlan(
                MakeSnapshot([]), MakeReadResult([]), new[] { MakeDisplay(TargetDevice) },
                MakeCapabilities(), "   "));
}
