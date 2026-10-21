using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopObservations;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.ExplorerGridPhysicalProbe;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.Tests.ExplorerGridPhysicalProbe;

public sealed class ExplorerGridPhysicalProbeRunnerTests
{
    [Fact]
    public async Task RunAsync_TwoDisplaysWithDifferentWorkAreaAnchors_PassesWithPerDisplayPhase()
    {
        var state = FakeDesktopState.CreateTwoDisplays();
        using var sta = new StaThreadExecutor();
        var store = new FakeShortcutStore(state);
        var runner = CreateRunner(state, sta, store);

        var report = await runner.RunAsync();

        Assert.Equal(ExplorerGridProbeRunStatus.Passed, report.Status);
        Assert.True(report.SharedSpacing);
        Assert.True(report.PhaseVerifiedPerDisplay);
        Assert.True(report.OwnedShortcutsDeleted);
        Assert.True(report.UserIconsUnchanged);
        Assert.Equal(2, report.Displays.Count);
        Assert.All(report.Displays, result =>
        {
            Assert.Equal(new PixelPoint(100, 100), result.Spacing);
            Assert.Equal(3, result.HorizontalSampleCount);
            Assert.Equal(3, result.VerticalSampleCount);
            Assert.Equal(4, result.CornerSampleCount);
        });
        // 两块屏的工作区原点不同 → 绝对相位不同（真机就是 (2,16) vs (35,84)）：
        // 这不再让 Probe 失败，相位只要求逐屏可信。
        // DISPLAY-1 工作区左=0 → 锚点 10；DISPLAY-2 工作区左=450 → 锚点 460，460 mod 100 = 60。
        Assert.Equal(
            new PixelPoint(10, 20),
            report.Displays.Single(display => display.DeviceName == "DISPLAY-1").Phase);
        Assert.Equal(
            new PixelPoint(60, 20),
            report.Displays.Single(display => display.DeviceName == "DISPLAY-2").Phase);
        Assert.All(
            report.Moves,
            move => Assert.StartsWith("c:\\probe\\desktopagent grid probe ", move.ShortcutStableId));
        Assert.Equal(2, report.Moves.Count(move => move.Kind == ExplorerGridProbeMoveKind.Restore));
        var firstSeed = Assert.Single(
            report.Moves,
            move => move.DisplayDeviceName == "DISPLAY-1" && move.Kind == ExplorerGridProbeMoveKind.Seed);
        Assert.Equal(firstSeed.Requested, firstSeed.Observed);
        Assert.NotEqual(firstSeed.Observed, firstSeed.SettledObserved);
        Assert.Equal(new PixelPoint(210, 220), firstSeed.SettledObserved);
        var firstHorizontal = report.Moves.First(move =>
            move.DisplayDeviceName == "DISPLAY-1" &&
            move.Kind == ExplorerGridProbeMoveKind.Horizontal);
        Assert.Equal(new PixelPoint(210, 220), firstHorizontal.Requested);
        Assert.Equal(new PixelPoint(310, 320), state.Icons[FakeDesktopState.UserStableId].PixelPosition);
        Assert.DoesNotContain(state.Icons.Keys, key => key.Contains("grid probe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_TopologyChangesAfterFirstMove_StopsBeforeSecondScanMove()
    {
        var state = FakeDesktopState.CreateOneDisplay();
        state.AfterWriterMove = call =>
        {
            if (call == 1)
            {
                state.Displays =
                [
                    state.Displays[0] with
                    {
                        WorkArea = new PixelRect(0, 0, 390, 400),
                    },
                ];
            }
        };
        using var sta = new StaThreadExecutor();
        var store = new FakeShortcutStore(state);
        var runner = CreateRunner(state, sta, store);

        var report = await runner.RunAsync();

        Assert.Equal(ExplorerGridProbeRunStatus.Failed, report.Status);
        Assert.Contains("显示拓扑", report.Failure, StringComparison.Ordinal);
        Assert.Single(report.Moves, move => move.Kind != ExplorerGridProbeMoveKind.Restore);
        Assert.True(report.OwnedShortcutsDeleted);
        Assert.DoesNotContain(state.Icons.Keys, key => key.Contains("grid probe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ObservedPositionDriftsOffTheLattice_SkipsThatSampleAndStillPasses()
    {
        var state = FakeDesktopState.CreateOneDisplay();
        // 放大工作区：丢弃一个落点不该把该轴样本数压到每轴 ≥3 的门槛以下
        // （这个用例验证的是"丢弃不中止整轮"，不是样本门槛本身）。
        state.Displays = [state.Displays[0] with { WorkArea = new PixelRect(0, 0, 700, 400) }];
        // 落点既不是请求槽位、也不在晶格上（真机上 Explorer 会把它挪到另一套旧晶格）：
        // 重试一次；仍偏离就丢弃该样本继续扫描——偏离本身不是几何矛盾，安全条件另有校验。
        state.ActualPositionOverride = (call, requested, normal) =>
            call is 3 or 4 ? new PixelPoint(normal.X + 1, normal.Y) : normal;
        using var sta = new StaThreadExecutor();
        var store = new FakeShortcutStore(state);
        var runner = CreateRunner(state, sta, store);

        var report = await runner.RunAsync();

        Assert.True(
            report.Status == ExplorerGridProbeRunStatus.Passed,
            $"status={report.Status} failure={report.Failure}\n{report.Format()}");
        Assert.Equal(1, report.Displays.Single().SkippedLandingCount);
        Assert.Contains(report.Moves, move => move.SettledIsExact == false);
        Assert.True(report.OwnedShortcutsDeleted);
        Assert.True(report.UserIconsUnchanged);
    }

    [Fact]
    public async Task RunAsync_ExplorerPutsTheIconBackOnItsRememberedSlot_RetriesOnceAndStillCountsTheSample()
    {
        var state = FakeDesktopState.CreateOneDisplay();
        // 真机观测（第三次实机运行）：Explorer 会异步回写它自己保存的桌面布局，偶尔把探针快捷方式放回
        // 它"记住"的槽位。落点仍是本屏晶格上的另一个 native slot → 重试一次；第二次被接受，样本仍然有效。
        state.ActualPositionOverride = (call, _, normal) =>
            call == 3 ? NeighbourSlotOnTheSameLattice(normal) : normal;
        using var sta = new StaThreadExecutor();
        var store = new FakeShortcutStore(state);
        var runner = CreateRunner(state, sta, store);

        var report = await runner.RunAsync();

        Assert.Equal(ExplorerGridProbeRunStatus.Passed, report.Status);
        var display = report.Displays.Single();
        Assert.Equal(0, display.SkippedLandingCount);
        Assert.True(display.PhaseVerified);
        Assert.True(report.UserIconsUnchanged);

        var retried = report.Moves
            .Where(move => move.Kind != ExplorerGridProbeMoveKind.Restore)
            .GroupBy(move => (move.Kind, move.Ordinal))
            .Where(group => group.Count() == 2)
            .ToArray();
        Assert.Single(retried);
        Assert.False(retried[0].First().SettledIsExact);
        Assert.True(retried[0].Last().SettledIsExact);
    }

    [Fact]
    public async Task RunAsync_ExplorerKeepsPuttingTheIconBack_SkipsThatSampleAndStillPasses()
    {
        var state = FakeDesktopState.CreateOneDisplay();
        // 放大工作区：丢弃一个落点后该轴仍有 ≥3 个样本，验证的是丢弃语义而不是样本门槛。
        state.Displays = [state.Displays[0] with { WorkArea = new PixelRect(0, 0, 700, 400) }];
        // 重试后被放回同一个"记得的槽位"：该落点不计数（缺口可见于 SkippedLandingCount），
        // 但不该让整轮失败——前面的落点已经测准了间距与相位。
        state.ActualPositionOverride = (call, _, normal) =>
            call is 3 or 4 ? NeighbourSlotOnTheSameLattice(normal) : normal;
        using var sta = new StaThreadExecutor();
        var store = new FakeShortcutStore(state);
        var runner = CreateRunner(state, sta, store);

        var report = await runner.RunAsync();

        Assert.True(
            report.Status == ExplorerGridProbeRunStatus.Passed,
            $"status={report.Status} failure={report.Failure}\n{report.Format()}");
        var display = report.Displays.Single();
        Assert.Equal(1, display.SkippedLandingCount);
        Assert.True(display.PhaseVerified);
        Assert.True(report.UserIconsUnchanged);
        // 被跳过的落点不计入 active 样本：active 只统计"确实落在请求槽位"的落点。
        var exactHorizontal = report.Moves.Count(move =>
            move.Kind == ExplorerGridProbeMoveKind.Horizontal && move.SettledIsExact == true);
        Assert.Equal(display.ActiveHorizontalSampleCount, exactHorizontal);
    }

    /// <summary>把落点挪到同一晶格上的相邻槽位：模拟 Explorer 把它放回"记得的槽位"。</summary>
    private static PixelPoint NeighbourSlotOnTheSameLattice(PixelPoint normal)
        => normal.X + 100 <= 399
            ? new PixelPoint(normal.X + 100, normal.Y)
            : new PixelPoint(normal.X - 100, normal.Y);

    [Fact]
    public async Task RunAsync_SettledLandingLeavesTheWorkArea_SkipsThatSampleAndStillPasses()
    {
        var state = FakeDesktopState.CreateOneDisplay();
        // 放大工作区：越界丢弃一个落点后该轴仍有 ≥3 个样本，验证的是丢弃语义而不是样本门槛。
        state.Displays = [state.Displays[0] with { WorkArea = new PixelRect(0, 0, 700, 400) }];
        // 真机第五次运行：Explorer 把 settled 落点回写到目标屏 WorkArea 之外。
        // 只要位置安全性成立（用户图标未动、其他探针未动），这就是"这一格没测到"：
        // 重试一次后仍越界就丢弃该样本并继续扫描，而不是中止整轮。
        state.ActualPositionOverride = (call, _, normal) =>
            call is 3 or 4 ? new PixelPoint(-50, normal.Y) : normal;
        using var sta = new StaThreadExecutor();
        var store = new FakeShortcutStore(state);
        var runner = CreateRunner(state, sta, store);

        var report = await runner.RunAsync();

        Assert.True(
            report.Status == ExplorerGridProbeRunStatus.Passed,
            $"status={report.Status} failure={report.Failure}\n{report.Format()}");
        var display = report.Displays.Single();
        Assert.True(display.IsMeasured);
        Assert.Equal(1, display.SkippedLandingCount);
        var exactHorizontal = report.Moves.Count(move =>
            move.Kind == ExplorerGridProbeMoveKind.Horizontal && move.SettledIsExact == true);
        Assert.Equal(display.ActiveHorizontalSampleCount, exactHorizontal);
        Assert.True(report.UserIconsUnchanged);
        Assert.True(report.OwnedShortcutsDeleted);
    }

    [Fact]
    public async Task RunAsync_ExplorerPutsTheSeedOnAnotherDisplay_MarksThatDisplayUnmeasuredAndKeepsTheOthers()
    {
        var state = FakeDesktopState.CreateTwoDisplays();
        // 真机第五次运行：DISPLAY2 的 seed 请求立即落点正确，settled 却被回写到 DISPLAY1
        // （Explorer 把自己记得的桌面布局应用到探针快捷方式上）。
        // 这不是几何矛盾，而是"这块屏测不到"：该屏不写事实，其余屏照测。
        state.ActualPositionOverride = (_, target, normal) =>
            target.X >= 450 ? new PixelPoint(10, 20) : normal;
        using var sta = new StaThreadExecutor();
        var store = new FakeShortcutStore(state);
        var runner = CreateRunner(state, sta, store);

        var report = await runner.RunAsync();

        Assert.True(
            report.Status == ExplorerGridProbeRunStatus.Partial,
            $"status={report.Status} failure={report.Failure}\n{report.Format()}");
        Assert.Null(report.Failure);
        Assert.Contains("UnmeasuredDisplays: DISPLAY-2", report.Format(), StringComparison.Ordinal);

        // 逐屏事实：可测的屏照测（继续拿到样本），不可测的屏显式标记并在报告里留原因。
        var measured = report.Displays.Single(display => display.DeviceName == "DISPLAY-1");
        Assert.True(measured.IsMeasured);
        Assert.True(measured.ActiveScanSampleCount > 0);
        Assert.True(measured.PhaseVerified);

        var unmeasured = report.Displays.Single(display => display.DeviceName == "DISPLAY-2");
        Assert.False(unmeasured.IsMeasured);
        Assert.False(unmeasured.PhaseVerified);
        Assert.NotNull(unmeasured.UnmeasuredReason);
        Assert.Contains("DISPLAY-2", unmeasured.UnmeasuredReason!, StringComparison.Ordinal);
        Assert.Equal(0, unmeasured.ActiveScanSampleCount);

        Assert.Equal(2, report.Displays.Count);
        Assert.True(report.UserIconsUnchanged);
        Assert.True(report.OwnedShortcutsDeleted);
        Assert.DoesNotContain(state.Icons.Keys, key => key.Contains("grid probe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ExplorerKeepsEveryLandingOffTheSlot_MarksThatDisplayUnmeasured()
    {
        var state = FakeDesktopState.CreateTwoDisplays();
        // DISPLAY2 的每次落点都被挪开 5px ⇒ 该屏永远拿不到主动样本，
        // 但 DISPLAY1 的实测不被牵连（逐屏写事实）。
        state.ActualPositionOverride = (_, target, normal) =>
            target.X >= 450 ? new PixelPoint(target.X + 7, target.Y + 3) : normal;
        using var sta = new StaThreadExecutor();
        var store = new FakeShortcutStore(state);
        var runner = CreateRunner(state, sta, store);

        var report = await runner.RunAsync();

        Assert.True(
            report.Status == ExplorerGridProbeRunStatus.Partial,
            $"status={report.Status} failure={report.Failure}\n{report.Format()}");
        var unmeasured = report.Displays.Single(display => display.DeviceName == "DISPLAY-2");
        Assert.False(unmeasured.IsMeasured);
        Assert.Contains("主动落点样本不足", unmeasured.UnmeasuredReason!, StringComparison.Ordinal);
        Assert.True(report.Displays.Single(display => display.DeviceName == "DISPLAY-1").IsMeasured);
    }

    [Fact]
    public async Task RunAsync_OccupiedNativeSlot_UsesPassiveSampleWithoutMovingUserIcon()
    {
        var state = FakeDesktopState.CreateOneDisplay();
        state.Displays =
        [
            state.Displays[0] with
            {
                Bounds = new PixelRect(0, 0, 500, 500),
                WorkArea = new PixelRect(0, 0, 500, 500),
            },
        ];
        const string blockerId = "c:\\desktop\\occupied.txt";
        state.Icons.Add(
            blockerId,
            new DesktopIconDescriptor(
                blockerId,
                "occupied.txt",
                "C:\\Desktop\\occupied.txt",
                ItemKind.File,
                new PixelPoint(10, 220),
                "DISPLAY-1",
                IsSystemItem: false,
                IsFileSystemItem: true));
        using var sta = new StaThreadExecutor();
        var store = new FakeShortcutStore(state);
        var runner = CreateRunner(state, sta, store);

        var report = await runner.RunAsync();

        Assert.Equal(ExplorerGridProbeRunStatus.Passed, report.Status);
        Assert.Contains(report.PassiveSamples, sample =>
            sample.Kind == ExplorerGridProbeMoveKind.Horizontal &&
            sample.Observed == new PixelPoint(10, 220) &&
            sample.OccupantStableId == blockerId);
        Assert.DoesNotContain(report.Moves, move =>
            move.Kind == ExplorerGridProbeMoveKind.Horizontal &&
            move.Requested == new PixelPoint(10, 220));
        Assert.Equal(new PixelPoint(10, 220), state.Icons[blockerId].PixelPosition);
        Assert.True(report.UserIconsUnchanged);
    }

    [Fact]
    public async Task RunAsync_ExistingIconsOnADifferentLattice_MarksTheOnlyDisplayUnmeasuredAndWritesNothing()
    {
        var state = FakeDesktopState.CreateOneDisplay();
        for (var index = 1; index <= 3; index++)
        {
            AddExistingIcon(
                state,
                $"c:\\desktop\\shifted{index}.txt",
                $"shifted{index}.txt",
                "DISPLAY-1",
                new PixelPoint(60, 70 + (index - 1) * 100));
        }

        using var sta = new StaThreadExecutor();
        var store = new FakeShortcutStore(state);
        var runner = CreateRunner(state, sta, store);

        var report = await runner.RunAsync();

        // 探针落点晶格 (10,20) 与该屏既有图标晶格 (60,70) 不一致：测量无法代表该屏 native slot，
        // 所以这块屏不写事实。一个屏都没测到时整轮 Failed（fail closed），而不是当成几何矛盾中途炸掉。
        Assert.True(
            report.Status == ExplorerGridProbeRunStatus.Failed,
            $"status={report.Status} failure={report.Failure}\n{report.Format()}");
        var display = Assert.Single(report.Displays);
        Assert.False(display.IsMeasured);
        Assert.NotNull(display.UnmeasuredReason);
        Assert.Contains("不一致", display.UnmeasuredReason, StringComparison.Ordinal);
        Assert.Contains("没有拿到实测样本", report.Failure, StringComparison.Ordinal);
        Assert.True(report.OwnedShortcutsDeleted);
        Assert.DoesNotContain(state.Icons.Keys, key => key.Contains("grid probe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ExplorerReflowsTheDesktopDuringTheRun_PutsTheMovedUserIconsBackAndStillPasses()
    {
        var state = FakeDesktopState.CreateOneDisplay();
        var userOriginal = state.Icons[FakeDesktopState.UserStableId].PixelPosition;
        var reflowed = false;
        // 真机实测（2026-10-14）：探针写入的过程中 Explorer 会把既有图标流整体前移一个槽位
        // （左侧任务栏条带里的 46 个图标各移一个槽位），删掉专用快捷方式并不会让它自己复位。
        // 这里复刻"第一次写入之后漂移一次"：探针必须在收尾时把用户图标放回原位并复核通过。
        state.AfterWriterMove = _ =>
        {
            if (reflowed)
                return;
            reflowed = true;
            var icon = state.Icons[FakeDesktopState.UserStableId];
            state.Icons[FakeDesktopState.UserStableId] =
                icon with { PixelPosition = new PixelPoint(userOriginal.X, userOriginal.Y + 200) };
        };
        using var sta = new StaThreadExecutor();
        var store = new FakeShortcutStore(state);
        var runner = CreateRunner(state, sta, store);

        var report = await runner.RunAsync();

        Assert.Equal(ExplorerGridProbeRunStatus.Passed, report.Status);
        Assert.True(report.UserIconsUnchanged);
        Assert.True(
            report.RepairedUserIconCount >= 1,
            $"repaired={report.RepairedUserIconCount} unrepaired={report.UnrepairedUserIconCount} failure={report.Failure}");
        Assert.Equal(0, report.UnrepairedUserIconCount);
        Assert.Equal(userOriginal, state.Icons[FakeDesktopState.UserStableId].PixelPosition);
    }

    [Fact]
    public async Task RunAsync_UserIconsKeepMovingAfterTheRepair_ReportsTheUnrepairedIconsAndFails()
    {
        var state = FakeDesktopState.CreateOneDisplay();
        // 每一轮写入之后用户图标都被再挪走一次：恢复永远追不上，必须判 Failed 并报出未恢复的数量，
        // 绝不能把"桌面还在动"当成"桌面没变"。
        state.AfterWriterMove = _ =>
        {
            var icon = state.Icons[FakeDesktopState.UserStableId];
            var moved = icon with
            {
                PixelPosition = new PixelPoint(icon.PixelPosition.X, icon.PixelPosition.Y + 100),
            };
            state.Icons[FakeDesktopState.UserStableId] = moved;
            // 连 settled Observation 也停在被挪走的位置：模拟"桌面一直在动"，恢复动作永远追不上。
            state.PendingSettledPositions[FakeDesktopState.UserStableId] = moved.PixelPosition;
        };
        using var sta = new StaThreadExecutor();
        var store = new FakeShortcutStore(state);
        var runner = CreateRunner(state, sta, store);

        var report = await runner.RunAsync();

        Assert.Equal(ExplorerGridProbeRunStatus.Failed, report.Status);
        Assert.False(report.UserIconsUnchanged);
        Assert.True(
            report.UnrepairedUserIconCount >= 1,
            $"repaired={report.RepairedUserIconCount} unrepaired={report.UnrepairedUserIconCount} failure={report.Failure}");
        Assert.Contains(
            "恢复到运行前的位置",
            report.Failure ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_IsDiagnosticsOnlyOrchestrator_WithoutRecoveryOrAiDependencies()
    {
        var parameterTypes = typeof(ExplorerGridPhysicalProbeRunner)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(static parameter => parameter.ParameterType)
            .ToArray();

        Assert.Contains(typeof(IDesktopObservationSource), parameterTypes);
        Assert.Contains(typeof(IStableDesktopObservationSource), parameterTypes);
        Assert.Contains(typeof(IDesktopIconPositionWriter), parameterTypes);
        Assert.Contains(typeof(IDisplayTopologyReader), parameterTypes);
        Assert.DoesNotContain(parameterTypes, type => type.Name.Contains("Recovery", StringComparison.Ordinal));
        Assert.DoesNotContain(parameterTypes, type => type.Name.Contains("Intelligence", StringComparison.Ordinal));
        Assert.DoesNotContain(parameterTypes, type => type.Name.Contains("Workspace", StringComparison.Ordinal));
    }

    private static ExplorerGridPhysicalProbeRunner CreateRunner(
        FakeDesktopState state,
        StaThreadExecutor sta,
        IExplorerGridProbeShortcutStore store)
    {
        var observationSource = new FakeObservationSource(state);
        var stableSource = new StableDesktopObservationSource(
            observationSource,
            new DesktopObservationStabilitySettings(TimeSpan.Zero),
            TimeProvider.System);
        return new ExplorerGridPhysicalProbeRunner(
            new FakeTopologyReader(state),
            observationSource,
            stableSource,
            new FakeWriter(state),
            sta,
            store,
            TimeProvider.System);
    }

    [Fact]
    public async Task RunAsync_ExplorerReflowsTheDesktopBetweenTwoSamples_KeepsWaitingAndStillPasses()
    {
        var state = FakeDesktopState.CreateOneDisplay();
        var originalUserPosition = state.Icons[FakeDesktopState.UserStableId].PixelPosition;
        // 真机第 7 次运行：探针写入开始后 Explorer 把 8 个既有图标挪走，稳定观测连续 4 轮失败并中止了整轮
        // （桌面已经被挪动，却连测量都没做完）。位移只发生一次，随后桌面安静下来。
        var displaced = false;
        state.AfterCapture = _ =>
        {
            if (state.WriterCallCount == 0 || displaced)
                return;
            displaced = true;
            var icon = state.Icons[FakeDesktopState.UserStableId];
            state.Icons[FakeDesktopState.UserStableId] = icon with
            {
                PixelPosition = new PixelPoint(icon.PixelPosition.X, icon.PixelPosition.Y + 50),
            };
        };
        using var sta = new StaThreadExecutor();
        var store = new FakeShortcutStore(state);
        var runner = CreateRunner(state, sta, store);

        var report = await runner.RunAsync();

        Assert.True(displaced, "本轮没有在两次采样之间制造桌面位移。");
        Assert.Equal(ExplorerGridProbeRunStatus.Passed, report.Status);
        Assert.True(report.UserIconsUnchanged, report.Failure);
        Assert.Equal(1, report.RepairedUserIconCount);
        Assert.Equal(0, report.UnrepairedUserIconCount);
        Assert.Equal(originalUserPosition, state.Icons[FakeDesktopState.UserStableId].PixelPosition);
    }

    [Fact]
    public async Task RunAsync_ExplorerMovesTheOtherProbeShortcutsDuringTheRun_KeepsScanningAndStillPasses()
    {
        // 真机证据（2026-10-14 第 7/8 次运行）：Explorer 的布局回写不只挪既有图标，
        // 也会把本次运行自己创建的其它专用快捷方式一起挪走，而且就发生在某个落点的双采样之间。
        // 这不该让整轮扫描作废——它既不是几何矛盾，也不涉及用户图标。
        var state = FakeDesktopState.CreateTwoDisplays();
        const string otherProbeStableId = "c:\\probe\\desktopagent grid probe test 02.url";
        var displaced = false;
        state.AfterCapture = _ =>
        {
            if (displaced || state.WriterCallCount == 0)
                return;

            var icon = state.Icons[otherProbeStableId];
            var moved = icon with
            {
                PixelPosition = new PixelPoint(icon.PixelPosition.X, icon.PixelPosition.Y + 100),
            };
            state.Icons[otherProbeStableId] = moved;
            state.PendingSettledPositions[otherProbeStableId] = moved.PixelPosition;
            displaced = true;
        };
        using var sta = new StaThreadExecutor();
        var store = new FakeShortcutStore(state);
        var runner = CreateRunner(state, sta, store);

        var report = await runner.RunAsync();

        Assert.True(displaced);
        Assert.True(
            report.Status == ExplorerGridProbeRunStatus.Passed,
            $"status={report.Status} failure={report.Failure}");
        Assert.Equal(2, report.Displays.Count);
        Assert.All(report.Displays, display => Assert.True(display.IsMeasured, report.Format()));
    }

    [Fact]
    public async Task RunAsync_TheDisplaysOwnIconsAreOnAnotherLattice_MarksThatDisplayUnmeasuredAndKeepsTheOthers()
    {
        // 真机 DISPLAY3 的情形：探针自己的 native slot 推断出 phase=(2,16)，而该屏 46/48 个既有图标
        // 都在 phase=(35,84) 的晶格上。测出来的落点不能代表该屏图标所在的网格，所以该屏不写事实；
        // 但这只是"这块屏测不到"，不是几何矛盾，不该让整轮作废（事实本来就是逐屏写的）。
        var state = FakeDesktopState.CreateTwoDisplays();
        AddExistingIcon(state, "c:\\desktop\\offlattice-a.txt", "offlattice-a.txt", "DISPLAY-2", new PixelPoint(470, 30));
        AddExistingIcon(state, "c:\\desktop\\offlattice-b.txt", "offlattice-b.txt", "DISPLAY-2", new PixelPoint(570, 130));
        using var sta = new StaThreadExecutor();
        var store = new FakeShortcutStore(state);
        var runner = CreateRunner(state, sta, store);

        var report = await runner.RunAsync();

        Assert.True(
            report.Status == ExplorerGridProbeRunStatus.Partial,
            $"status={report.Status} failure={report.Failure}\n{report.Format()}");
        var measured = report.Displays.Single(display => display.DeviceName == "DISPLAY-1");
        var unmeasured = report.Displays.Single(display => display.DeviceName == "DISPLAY-2");
        Assert.True(measured.IsMeasured, report.Format());
        Assert.False(unmeasured.IsMeasured);
        Assert.NotNull(unmeasured.UnmeasuredReason);
        Assert.Contains("不一致", unmeasured.UnmeasuredReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_SeedRequestUsesTheDisplaysOwnIconLattice_NotTheProbeShortcutsLanding()
    {
        // 真机第 5~9 次运行：探针快捷方式（FakeShortcutStore 也把它建在 DISPLAY-1）会被 Explorer 摆到别的屏，
        // 旧代码因此用"探针自己的落点"推断 seed 候选槽位的相位（= DISPLAY-1 的 10/20 晶格），把目标屏的
        // 候选槽位算到别的屏的晶格上（真机 DISPLAY3 就是这样被推离自己晶格的）。
        // 现在改用该屏既有图标形成的可靠晶格：DISPLAY-2 工作区左=450 → 该屏自己的晶格相位是 (60,20)。
        // 中心槽位 (850,300) 被既有图标占住，所以这里走的正是"带相位提示枚举 native slot"那条路。
        var state = FakeDesktopState.CreateTwoDisplays();
        state.Displays =
        [
            state.Displays[0],
            state.Displays[1] with
            {
                Bounds = new PixelRect(450, 0, 1250, 600),
                WorkArea = new PixelRect(450, 0, 1250, 600),
            },
        ];
        AddExistingIcon(state, "c:\\desktop\\d2-a.txt", "d2-a.txt", "DISPLAY-2", new PixelPoint(460, 20));
        AddExistingIcon(state, "c:\\desktop\\d2-b.txt", "d2-b.txt", "DISPLAY-2", new PixelPoint(460, 120));
        AddExistingIcon(state, "c:\\desktop\\d2-c.txt", "d2-c.txt", "DISPLAY-2", new PixelPoint(460, 220));
        AddExistingIcon(state, "c:\\desktop\\d2-d.txt", "d2-d.txt", "DISPLAY-2", new PixelPoint(860, 320));
        using var sta = new StaThreadExecutor();
        var store = new FakeShortcutStore(state);
        var runner = CreateRunner(state, sta, store);

        var report = await runner.RunAsync();

        Assert.True(
            report.Status == ExplorerGridProbeRunStatus.Passed,
            $"status={report.Status} failure={report.Failure}\n{report.Format()}");
        var seed = Assert.Single(
            report.Moves,
            move => move.DisplayDeviceName == "DISPLAY-2" && move.Kind == ExplorerGridProbeMoveKind.Seed);
        Assert.Equal(
            new PixelPoint(60, 20),
            new PixelPoint(
                PositiveModulo(seed.Requested.X, state.Spacing.X),
                PositiveModulo(seed.Requested.Y, state.Spacing.Y)));
        Assert.True(
            report.Displays.Single(display => display.DeviceName == "DISPLAY-2").IsMeasured,
            report.Format());
    }

    private static void AddExistingIcon(
        FakeDesktopState state,
        string stableId,
        string displayName,
        string monitorDeviceName,
        PixelPoint position)
        => state.Icons.Add(
            stableId,
            new DesktopIconDescriptor(
                stableId,
                displayName,
                stableId,
                ItemKind.File,
                position,
                monitorDeviceName,
                IsSystemItem: false,
                IsFileSystemItem: true));

    private sealed class FakeDesktopState
    {
        public const string UserStableId = "c:\\desktop\\user.txt";

        public IReadOnlyList<DisplayDescriptor> Displays { get; set; } = [];
        public Dictionary<string, DesktopIconDescriptor> Icons { get; } = new(StringComparer.Ordinal);
        public PixelPoint Spacing { get; } = new(100, 100);
        public int WriterCallCount { get; set; }
        public Action<int>? AfterWriterMove { get; set; }
        public Func<int, PixelPoint, PixelPoint, PixelPoint>? ActualPositionOverride { get; set; }
        public Dictionary<string, PixelPoint> PendingSettledPositions { get; } = new(StringComparer.Ordinal);

        /// <summary>每次完整观察结束后触发，用来模拟"两次采样之间 Explorer 改动了桌面"。</summary>
        public Action<int>? AfterCapture { get; set; }

        public int CaptureCount { get; set; }

        public static FakeDesktopState CreateOneDisplay()
        {
            var state = new FakeDesktopState
            {
                Displays =
                [
                    new DisplayDescriptor(
                        "DISPLAY-1",
                        new PixelRect(0, 0, 400, 400),
                        new PixelRect(0, 0, 400, 400),
                        96,
                        96,
                        IsPrimary: true),
                ],
            };
            state.AddUserIcon();
            return state;
        }

        public static FakeDesktopState CreateTwoDisplays()
        {
            var state = CreateOneDisplay();
            state.Displays =
            [
                state.Displays[0],
                new DisplayDescriptor(
                    "DISPLAY-2",
                    new PixelRect(450, 0, 850, 400),
                    new PixelRect(450, 0, 850, 400),
                    96,
                    96,
                    IsPrimary: false),
            ];
            return state;
        }

        public PixelPoint Snap(string? monitorDeviceName, PixelPoint requested)
        {
            var workArea = Displays
                .FirstOrDefault(display => string.Equals(
                    display.DeviceName,
                    monitorDeviceName,
                    StringComparison.Ordinal))
                ?.WorkArea ?? new PixelRect(0, 0, 0, 0);

            // 相位锚定在"该屏自己的工作区原点"上：任务栏只占一块屏时，
            // 两块屏的绝对相位本来就不同，这正是真机 (2,16) vs (35,84) 的情形。
            var x = SnapAxis(requested.X, Spacing.X, workArea.Left + 10);
            var y = SnapAxis(requested.Y, Spacing.Y, workArea.Top + 20);
            return new PixelPoint(x, y);
        }

        private void AddUserIcon()
        {
            Icons.Add(
                UserStableId,
                new DesktopIconDescriptor(
                    UserStableId,
                    "user.txt",
                    "C:\\Desktop\\user.txt",
                    ItemKind.File,
                    new PixelPoint(310, 320),
                    "DISPLAY-1",
                    IsSystemItem: false,
                    IsFileSystemItem: true));
        }

        private static int SnapAxis(int value, int spacing, int phase)
            => checked(phase + (int)Math.Round((value - phase) / (double)spacing) * spacing);
    }

    private sealed class FakeTopologyReader(FakeDesktopState state) : IDisplayTopologyReader
    {
        public IReadOnlyList<DisplayDescriptor> GetDisplays() => state.Displays.ToArray();
    }

    private sealed class FakeObservationSource(FakeDesktopState state) : IDesktopObservationSource
    {
        public Task<DesktopObservationCaptureResult> CaptureAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var pending in state.PendingSettledPositions.ToArray())
            {
                var icon = state.Icons[pending.Key];
                state.Icons[pending.Key] = icon with { PixelPosition = pending.Value };
                state.PendingSettledPositions.Remove(pending.Key);
            }
            var observation = new DesktopObservation(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                state.Displays,
                Capabilities(state),
                state.Icons.Values.ToArray(),
                ConnectionMethod.FindWindowSW,
                TimeSpan.Zero);
            state.CaptureCount++;
            state.AfterCapture?.Invoke(state.CaptureCount);
            return Task.FromResult(DesktopObservationCaptureResult.Captured(observation));
        }
    }

    private sealed class FakeWriter(FakeDesktopState state) : IDesktopIconPositionWriter
    {
        public DesktopIconPositioningCapabilities GetCapabilities() => Capabilities(state);

        public DesktopIconMoveResult MoveDesktopIcon(string stableId, PixelPoint targetPosition)
        {
            var icon = state.Icons[stableId];
            state.WriterCallCount++;
            // 真实 Explorer 把图标吸附到"该图标所在的显示器"的网格上：先定显示器，再吸附。
            var monitor = state.Displays
                .FirstOrDefault(display => display.WorkArea.Contains(targetPosition))
                ?.DeviceName ?? icon.MonitorDeviceName;
            var settledNormal = state.Snap(monitor, targetPosition);
            var settled = state.ActualPositionOverride?.Invoke(
                    state.WriterCallCount,
                    targetPosition,
                    settledNormal)
                ?? settledNormal;
            var immediate = targetPosition;
            state.Icons[stableId] = icon with { PixelPosition = immediate, MonitorDeviceName = monitor };
            state.PendingSettledPositions[stableId] = settled;
            state.AfterWriterMove?.Invoke(state.WriterCallCount);
            return new DesktopIconMoveResult(
                stableId,
                icon.PixelPosition,
                targetPosition,
                immediate,
                ConnectionMethod.FindWindowSW,
                null);
        }
    }

    private sealed class FakeShortcutStore(FakeDesktopState state) : IExplorerGridProbeShortcutStore
    {
        public Task<IReadOnlyList<ExplorerGridProbeShortcut>> CreateAsync(
            int count,
            CancellationToken cancellationToken)
        {
            var shortcuts = new List<ExplorerGridProbeShortcut>();
            for (var index = 0; index < count; index++)
            {
                var path = $"C:\\Probe\\DesktopAgent Grid Probe test {index + 1:D2}.url";
                var stableId = path.ToLowerInvariant();
                state.Icons.Add(
                    stableId,
                    new DesktopIconDescriptor(
                        stableId,
                        $"DesktopAgent Grid Probe test {index + 1:D2}",
                        path,
                        ItemKind.Shortcut,
                        new PixelPoint(10 + index * 100, 20),
                        "DISPLAY-1",
                        IsSystemItem: false,
                        IsFileSystemItem: true));
                shortcuts.Add(new ExplorerGridProbeShortcut(path));
            }

            return Task.FromResult<IReadOnlyList<ExplorerGridProbeShortcut>>(shortcuts);
        }

        public Task DeleteAsync(
            IReadOnlyList<ExplorerGridProbeShortcut> shortcuts,
            CancellationToken cancellationToken)
        {
            foreach (var shortcut in shortcuts)
                state.Icons.Remove(shortcut.FilePath.ToLowerInvariant());
            return Task.CompletedTask;
        }
    }

    private static DesktopIconPositioningCapabilities Capabilities(FakeDesktopState state)
        => new(
            isAutoArrangeEnabled: false,
            state.Spacing,
            ConnectionMethod.FindWindowSW,
            null);

    private static int PositiveModulo(int value, int modulus)
    {
        var remainder = value % modulus;
        return remainder < 0 ? remainder + modulus : remainder;
    }
}
