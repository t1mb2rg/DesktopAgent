using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Tests.DesktopLayouts.Restore.Execution;

/// <summary>
/// 「撤销上次整理」的只读前置判定。这里只测判定，不动桌面、不碰数据库。
/// </summary>
public sealed class DesktopLayoutRollbackPreflightTests
{
    private static readonly string Td = @"\\.\DISPLAY1";
    private static readonly string OtherTd = @"\\.\DISPLAY2";
    private static readonly PixelPoint Spacing = new(80, 80);
    private static readonly PixelRect WorkArea = new(0, 0, 1920, 1080);

    private static int _ordinal;

    private static DisplayDescriptor Display(string deviceName = null!)
        => new(deviceName ?? Td, new PixelRect(0, 0, 1920, 1080), WorkArea, 96, 96, true);

    private static DesktopIconSnapshotItem Snap(string stableId, PixelPoint position, string? deviceName = null)
        => new(_ordinal++, stableId, $"N_{stableId}", null, ItemKind.File, position, deviceName ?? Td, false, true);

    private static DesktopIconDescriptor Live(string stableId, PixelPoint position, string? deviceName = null)
        => new(stableId, $"N_{stableId}", null, ItemKind.File, position, deviceName ?? Td, false, true);

    private static DesktopIconPositioningCapabilities Capabilities()
        => new(false, Spacing, ConnectionMethod.FindWindowSW, null);

    private static DesktopLayoutSnapshot Snapshot(params DesktopIconSnapshotItem[] items)
        => new(Guid.NewGuid(), DateTimeOffset.UtcNow, Td, ConnectionMethod.FindWindowSW, 1, items);

    private static TargetDisplayLayoutExecutionPlan ExecutionPlan(
        params TargetDisplayLayoutExecutionStep[] steps)
        => new(
            Td,
            true,
            steps.Any(step => step.Kind == TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging),
            steps,
            Array.Empty<TargetDisplayLayoutStagingAssignment>(),
            Array.Empty<TargetDisplayLayoutExecutionPlanIssue>());

    private static TargetDisplayLayoutExecutionStep Move(int index, string stableId, PixelPoint from, PixelPoint to)
        => new(index, stableId, $"N_{stableId}", from, to, TargetDisplayLayoutExecutionStepKind.MoveToTarget, Td);

    /// <summary>构造一个已收尾的会话：快照位置 → 执行后位置，两个项目。</summary>
    private static RecoverySessionRecord CommittedSession(
        DesktopLayoutSnapshot snapshot,
        IReadOnlyList<DesktopIconDescriptor> liveAfterApply,
        params TargetDisplayLayoutExecutionStep[] steps)
    {
        var plan = TargetDisplayLayoutRestorePlanner.CreatePlan(
            snapshot,
            new DesktopIconReadResult(liveAfterApply, 0, ConnectionMethod.FindWindowSW, TimeSpan.Zero, null),
            new[] { Display() },
            Capabilities(),
            Td);

        return new RecoverySessionRecord
        {
            SessionId = Guid.NewGuid(),
            SafetySnapshotId = snapshot.SnapshotId,
            TargetDeviceName = Td,
            StartedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Lifecycle = RecoverySessionLifecycle.Completed,
            SourceRestorePlan = plan,
            ExecutionPlan = ExecutionPlan(steps),
            CompletedForwardStepIndexes = steps.Select(step => step.StepIndex).ToArray(),
            Revision = 1,
        };
    }

    [Fact]
    public void WithoutCommittedSession_ReportsNoCommittedSession()
    {
        var result = DesktopLayoutRollbackPreflight.Evaluate(
            session: null,
            snapshot: null,
            currentIcons: Array.Empty<DesktopIconDescriptor>(),
            currentDisplays: new[] { Display() });

        Assert.False(result.CanRollback);
        Assert.Equal(DesktopLayoutRollbackBlockReason.NoCommittedSession, result.Reason);
        Assert.Equal(Guid.Empty, result.SessionId);
        Assert.Empty(result.ChangedItemNames);
    }

    [Fact]
    public void UnfinishedSession_AsksToConvergeRecoveryFirst()
    {
        var snapshot = Snapshot(Snap("a", new PixelPoint(160, 240)));
        var session = CommittedSession(
            snapshot,
            new[] { Live("a", new PixelPoint(240, 320)) },
            Move(0, "a", new PixelPoint(160, 240), new PixelPoint(240, 320)))
            with { Lifecycle = RecoverySessionLifecycle.Executing };

        var result = DesktopLayoutRollbackPreflight.Evaluate(
            session,
            snapshot,
            new[] { Live("a", new PixelPoint(240, 320)) },
            new[] { Display() });

        Assert.False(result.CanRollback);
        Assert.Equal(DesktopLayoutRollbackBlockReason.SessionNotCompleted, result.Reason);
        Assert.Contains("处理恢复状态", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionThatMovedNothing_HasNothingToUndo()
    {
        var snapshot = Snapshot(Snap("a", new PixelPoint(160, 240)));
        var session = CommittedSession(
            snapshot,
            new[] { Live("a", new PixelPoint(160, 240)) },
            Move(0, "a", new PixelPoint(160, 240), new PixelPoint(240, 320))) with
        {
            CompletedForwardStepIndexes = Array.Empty<int>(),
        };

        var result = DesktopLayoutRollbackPreflight.Evaluate(
            session,
            snapshot,
            new[] { Live("a", new PixelPoint(240, 320)) },
            new[] { Display() });

        Assert.False(result.CanRollback);
        Assert.Equal(DesktopLayoutRollbackBlockReason.NothingWasMoved, result.Reason);
    }

    [Fact]
    public void MissingSafetySnapshot_RefusesInsteadOfGuessing()
    {
        var snapshot = Snapshot(Snap("a", new PixelPoint(160, 240)));
        var session = CommittedSession(
            snapshot,
            new[] { Live("a", new PixelPoint(240, 320)) },
            Move(0, "a", new PixelPoint(160, 240), new PixelPoint(240, 320)));

        var result = DesktopLayoutRollbackPreflight.Evaluate(
            session,
            snapshot: null,
            currentIcons: new[] { Live("a", new PixelPoint(240, 320)) },
            currentDisplays: new[] { Display() });

        Assert.False(result.CanRollback);
        Assert.Equal(DesktopLayoutRollbackBlockReason.SnapshotMissing, result.Reason);
    }

    [Fact]
    public void SnapshotOfAnotherDisplay_IsRefused()
    {
        var snapshot = new DesktopLayoutSnapshot(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            OtherTd,
            ConnectionMethod.FindWindowSW,
            1,
            new[] { Snap("a", new PixelPoint(160, 240)) });
        var session = CommittedSession(
            Snapshot(Snap("b", new PixelPoint(160, 240))),
            new[] { Live("b", new PixelPoint(240, 320)) },
            Move(0, "b", new PixelPoint(160, 240), new PixelPoint(240, 320)));

        var result = DesktopLayoutRollbackPreflight.Evaluate(
            session,
            snapshot,
            new[] { Live("b", new PixelPoint(240, 320)) },
            new[] { Display() });

        Assert.False(result.CanRollback);
        Assert.Equal(DesktopLayoutRollbackBlockReason.SnapshotNotForSameDisplay, result.Reason);
    }

    [Fact]
    public void TargetDisplayGoneFromTopology_IsRefused()
    {
        var snapshot = Snapshot(Snap("a", new PixelPoint(160, 240)));
        var session = CommittedSession(
            snapshot,
            new[] { Live("a", new PixelPoint(240, 320)) },
            Move(0, "a", new PixelPoint(160, 240), new PixelPoint(240, 320)));

        var result = DesktopLayoutRollbackPreflight.Evaluate(
            session,
            snapshot,
            new[] { Live("a", new PixelPoint(240, 320)) },
            currentDisplays: new[] { Display(OtherTd) });

        Assert.False(result.CanRollback);
        Assert.Equal(DesktopLayoutRollbackBlockReason.TargetDisplayMissing, result.Reason);
    }

    [Fact]
    public void ItemMovedByUserSinceApply_RefusesAndNamesIt()
    {
        var snapshot = Snapshot(
            Snap("a", new PixelPoint(160, 240)),
            Snap("b", new PixelPoint(160, 360)));
        var session = CommittedSession(
            snapshot,
            new[] { Live("a", new PixelPoint(240, 320)), Live("b", new PixelPoint(240, 440)) },
            Move(0, "a", new PixelPoint(160, 240), new PixelPoint(240, 320)),
            Move(1, "b", new PixelPoint(160, 360), new PixelPoint(240, 440)));

        var result = DesktopLayoutRollbackPreflight.Evaluate(
            session,
            snapshot,
            new[] { Live("a", new PixelPoint(240, 320)), Live("b", new PixelPoint(1000, 900)) },
            new[] { Display() });

        Assert.False(result.CanRollback);
        Assert.Equal(DesktopLayoutRollbackBlockReason.DesktopChangedSinceApply, result.Reason);
        Assert.Equal(new[] { "N_b" }, result.ChangedItemNames);
        Assert.Contains("N_b", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ItemCarriedToAnotherDisplaySinceApply_CountsAsChanged()
    {
        var snapshot = Snapshot(Snap("a", new PixelPoint(160, 240)));
        var session = CommittedSession(
            snapshot,
            new[] { Live("a", new PixelPoint(240, 320)) },
            Move(0, "a", new PixelPoint(160, 240), new PixelPoint(240, 320)));

        var result = DesktopLayoutRollbackPreflight.Evaluate(
            session,
            snapshot,
            new[] { Live("a", new PixelPoint(240, 320), OtherTd) },
            new[] { Display(), Display(OtherTd) });

        Assert.False(result.CanRollback);
        Assert.Equal(DesktopLayoutRollbackBlockReason.DesktopChangedSinceApply, result.Reason);
        Assert.Equal(new[] { "N_a" }, result.ChangedItemNames);
    }

    [Fact]
    public void AlreadyAtSnapshotPositions_ReportsNothingToUndo()
    {
        // 计划确实动过它（From≠To），但安全检查点里记的位置与执行后位置相同：
        // 这种情况没有可回退的位移，不能报「可以撤销」。
        var snapshot = Snapshot(Snap("a", new PixelPoint(240, 320)));
        var session = CommittedSession(
            snapshot,
            new[] { Live("a", new PixelPoint(240, 320)) },
            Move(0, "a", new PixelPoint(160, 240), new PixelPoint(240, 320)));

        var result = DesktopLayoutRollbackPreflight.Evaluate(
            session,
            snapshot,
            new[] { Live("a", new PixelPoint(240, 320)) },
            new[] { Display() });

        Assert.False(result.CanRollback);
        Assert.Equal(DesktopLayoutRollbackBlockReason.NothingToUndo, result.Reason);
    }

    [Fact]
    public void UntouchedDesktop_IsRollableAndReportsHowManyItemsMoveBack()
    {
        var snapshot = Snapshot(
            Snap("a", new PixelPoint(160, 240)),
            Snap("b", new PixelPoint(160, 360)),
            // 快照里有第三个项目，但这次执行没动它：不计入回退数量。
            Snap("c", new PixelPoint(160, 480)));
        var session = CommittedSession(
            snapshot,
            new[] { Live("a", new PixelPoint(240, 320)), Live("b", new PixelPoint(240, 440)) },
            Move(0, "a", new PixelPoint(160, 240), new PixelPoint(240, 320)),
            Move(1, "b", new PixelPoint(160, 360), new PixelPoint(240, 440)));

        var result = DesktopLayoutRollbackPreflight.Evaluate(
            session,
            snapshot,
            new[]
            {
                Live("a", new PixelPoint(240, 320)),
                Live("b", new PixelPoint(240, 440)),
                Live("c", new PixelPoint(160, 480)),
            },
            new[] { Display() });

        Assert.True(result.CanRollback);
        Assert.Equal(DesktopLayoutRollbackBlockReason.None, result.Reason);
        Assert.Equal(2, result.MoveBackCount);
        Assert.Equal(session.SessionId, result.SessionId);
        Assert.Equal(snapshot.SnapshotId, result.SafetySnapshotId);
        Assert.Equal(Td, result.TargetDeviceName);
        Assert.Empty(result.ChangedItemNames);
        Assert.Contains("2 个项目", result.Detail, StringComparison.Ordinal);
        Assert.Contains(Td, result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void StagedExecution_UsesFinalStepPositionAsExpectation()
    {
        var snapshot = Snapshot(Snap("a", new PixelPoint(160, 240)));
        var steps = new[]
        {
            new TargetDisplayLayoutExecutionStep(
                0, "a", "N_a", new PixelPoint(160, 240), new PixelPoint(400, 400),
                TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging, Td),
            new TargetDisplayLayoutExecutionStep(
                1, "a", "N_a", new PixelPoint(400, 400), new PixelPoint(240, 320),
                TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget, Td),
        };
        var session = CommittedSession(
            snapshot,
            new[] { Live("a", new PixelPoint(240, 320)) },
            steps);

        var result = DesktopLayoutRollbackPreflight.Evaluate(
            session,
            snapshot,
            new[] { Live("a", new PixelPoint(240, 320)) },
            new[] { Display() });

        Assert.True(result.CanRollback);
        Assert.Equal(1, result.MoveBackCount);
    }

    [Fact]
    public void StagedExecution_StillInStagingPosition_IsTreatedAsChanged()
    {
        var snapshot = Snapshot(Snap("a", new PixelPoint(160, 240)));
        var steps = new[]
        {
            new TargetDisplayLayoutExecutionStep(
                0, "a", "N_a", new PixelPoint(160, 240), new PixelPoint(400, 400),
                TargetDisplayLayoutExecutionStepKind.MoveToTemporaryStaging, Td),
            new TargetDisplayLayoutExecutionStep(
                1, "a", "N_a", new PixelPoint(400, 400), new PixelPoint(240, 320),
                TargetDisplayLayoutExecutionStepKind.MoveFromTemporaryStagingToTarget, Td),
        };
        var session = CommittedSession(
            snapshot,
            new[] { Live("a", new PixelPoint(400, 400)) },
            steps);

        var result = DesktopLayoutRollbackPreflight.Evaluate(
            session,
            snapshot,
            // 停在临时位：说明最后一笔没有落在终点，这时不能当作「还是我们整理完的样子」。
            new[] { Live("a", new PixelPoint(400, 400)) },
            new[] { Display() });

        Assert.False(result.CanRollback);
        Assert.Equal(DesktopLayoutRollbackBlockReason.DesktopChangedSinceApply, result.Reason);
    }

    [Fact]
    public void ApproveText_NamesTheCheckpointDisplay_NotTheRecordedTargetDisplay()
    {
        // 真机出现的形状：会话记的是「往 DISPLAY2 整理」，但检查点里这个项目本来在 DISPLAY1。
        // 撤销的落点是检查点，不是会话的目标屏——文案必须说 DISPLAY1。
        var snapshot = new DesktopLayoutSnapshot(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            OtherTd,
            ConnectionMethod.FindWindowSW,
            1,
            new[] { Snap("a", new PixelPoint(160, 240), Td) });
        var candidate = new DesktopLayoutRollbackCandidate(
            Guid.NewGuid(),
            DesktopLayoutRollbackSourceKind.RecoverySession,
            snapshot.SnapshotId,
            OtherTd,
            DateTimeOffset.UtcNow,
            new[] { new DesktopLayoutRollbackWrittenItem("a", "N_a", new PixelPoint(620, 940)) });

        var result = DesktopLayoutRollbackPreflight.EvaluateCandidate(
            candidate,
            snapshot,
            new[] { Live("a", new PixelPoint(620, 940), OtherTd) },
            new[] { Display(), Display(OtherTd) });

        Assert.True(result.CanRollback);
        Assert.Contains($"放回 {Td} 上整理前的位置", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain($"放回 {OtherTd} 上", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ApproveText_CrossDisplayUndo_NamesEveryCheckpointDisplay()
    {
        // 跨屏撤销：两个项目在检查点里分属两块屏，文案要点出「涉及 2 块屏」并列出屏名。
        var snapshot = Snapshot(
            Snap("a", new PixelPoint(160, 240), Td),
            Snap("b", new PixelPoint(160, 360), OtherTd));
        var candidate = new DesktopLayoutRollbackCandidate(
            Guid.NewGuid(),
            DesktopLayoutRollbackSourceKind.WorkspaceApply,
            snapshot.SnapshotId,
            null,
            DateTimeOffset.UtcNow,
            new[]
            {
                new DesktopLayoutRollbackWrittenItem("a", "N_a", new PixelPoint(240, 320)),
                new DesktopLayoutRollbackWrittenItem("b", "N_b", new PixelPoint(240, 440)),
            });

        var result = DesktopLayoutRollbackPreflight.EvaluateCandidate(
            candidate,
            snapshot,
            new[]
            {
                Live("a", new PixelPoint(240, 320), Td),
                Live("b", new PixelPoint(240, 440), Td),
            },
            new[] { Display(), Display(OtherTd) });

        Assert.True(result.CanRollback);
        Assert.Equal(2, result.MoveBackCount);
        Assert.Contains("涉及 2 块屏", result.Detail, StringComparison.Ordinal);
        Assert.Contains(Td, result.Detail, StringComparison.Ordinal);
        Assert.Contains(OtherTd, result.Detail, StringComparison.Ordinal);
    }
}
