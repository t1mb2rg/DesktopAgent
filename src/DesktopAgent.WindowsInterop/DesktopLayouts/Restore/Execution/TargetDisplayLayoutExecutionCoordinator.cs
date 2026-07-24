using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

/// <summary>
/// 布局恢复安全执行协调器。
/// 负责重新读取、重新规划、安全快照、逐步执行与保守回滚。
/// </summary>
public sealed class TargetDisplayLayoutExecutionCoordinator
{
    private readonly IDisplayTopologyReader _topologyReader;
    private readonly IDesktopIconReader _iconReader;
    private readonly IDesktopIconPositionWriter _positionWriter;
    private readonly StaThreadExecutor _staExecutor;
    private readonly DesktopLayoutSnapshotFactory _snapshotFactory;
    private readonly IDesktopLayoutSnapshotStore _snapshotStore;

    public TargetDisplayLayoutExecutionCoordinator(
        IDisplayTopologyReader topologyReader,
        IDesktopIconReader iconReader,
        IDesktopIconPositionWriter positionWriter,
        StaThreadExecutor staExecutor,
        DesktopLayoutSnapshotFactory snapshotFactory,
        IDesktopLayoutSnapshotStore snapshotStore)
    {
        _topologyReader = topologyReader ?? throw new ArgumentNullException(nameof(topologyReader));
        _iconReader = iconReader ?? throw new ArgumentNullException(nameof(iconReader));
        _positionWriter = positionWriter ?? throw new ArgumentNullException(nameof(positionWriter));
        _staExecutor = staExecutor ?? throw new ArgumentNullException(nameof(staExecutor));
        _snapshotFactory = snapshotFactory ?? throw new ArgumentNullException(nameof(snapshotFactory));
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
    }

    public async Task<TargetDisplayLayoutExecutionRunResult> ExecuteAsync(
        TargetDisplayLayoutRestorePlan restorePlan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(restorePlan);

        // ═══ 1. 重新读取 ═══
        DesktopIconPositioningCapabilities capabilities;
        DesktopIconReadResult currentRead;
        IReadOnlyList<DisplayDescriptor> currentDisplays;

        try
        {
            capabilities = await _staExecutor.RunAsync(() => _positionWriter.GetCapabilities())
                .WaitAsync(cancellationToken);
            currentRead = await _staExecutor.RunAsync(() => _iconReader.ReadDesktopIcons())
                .WaitAsync(cancellationToken);
            currentDisplays = _topologyReader.GetDisplays();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return TargetDisplayLayoutExecutionRunResult.FailedBeforeWrite(
                ex.GetType().Name, ex.Message);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ═══ 2. 重新生成执行计划 ═══
        var executionPlan = TargetDisplayLayoutExecutionPlanner.CreatePlan(
            restorePlan, currentRead, currentDisplays, capabilities);

        if (!executionPlan.IsExecutable)
        {
            return TargetDisplayLayoutExecutionRunResult.FailedBeforeWrite(
                "ExecutionPlanNotExecutable",
                executionPlan.Issues.Count > 0 ? executionPlan.Issues[0].Message : "生成不可执行计划。");
        }

        if (executionPlan.IsNoOp)
            return TargetDisplayLayoutExecutionRunResult.NoOp();

        cancellationToken.ThrowIfCancellationRequested();

        // ═══ 3-6. 安全快照 ═══
        var safetySnapshot = _snapshotFactory.Create(
            currentRead.Icons, restorePlan.TargetDeviceName, currentRead.ConnectionMethod);

        try { await _snapshotStore.SaveAsync(safetySnapshot, cancellationToken); }
        catch (Exception ex)
        {
            return TargetDisplayLayoutExecutionRunResult.FailedBeforeWrite(
                ex.GetType().Name, $"保存安全快照失败：{ex.Message}");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var readBack = await _snapshotStore.GetAsync(safetySnapshot.SnapshotId, cancellationToken);
        if (readBack is null)
            return TargetDisplayLayoutExecutionRunResult.FailedBeforeWrite(
                "SnapshotReadBackNull", "回读安全快照返回 null。");

        var verifyError = VerifySafetySnapshot(safetySnapshot, readBack);
        if (verifyError is not null)
            return TargetDisplayLayoutExecutionRunResult.FailedBeforeWrite(
                "SnapshotVerificationFailed", verifyError);

        cancellationToken.ThrowIfCancellationRequested();

        // ═══ 7. 创建恢复会话 ═══
        var session = CreateSession(restorePlan, executionPlan, safetySnapshot.SnapshotId);

        // ═══ 8. 逐步执行 ═══
        return await ExecuteStepsAsync(
            session, executionPlan, safetySnapshot.SnapshotId, cancellationToken);
    }

    // ═══════════════════════════════════════════════
    //  会话创建（深度冻结来源计划）
    // ═══════════════════════════════════════════════

    private static TargetDisplayLayoutRecoverySession CreateSession(
        TargetDisplayLayoutRestorePlan restorePlan,
        TargetDisplayLayoutExecutionPlan executionPlan,
        Guid safetySnapshotId)
    {
        // 深度冻结 Items
        var frozenItems = restorePlan.Items
            .Select(it => it)
            .ToList().AsReadOnly();

        // 深度冻结 Dependencies
        var frozenDeps = restorePlan.Dependencies.ToList().AsReadOnly();

        // 深度冻结 Cycles（含每个 Cycle 的 StableIds）
        var frozenCycles = restorePlan.Cycles
            .Select(c => new TargetDisplayLayoutRestoreCycle(
                Array.AsReadOnly(c.StableIds.ToArray())))
            .ToList().AsReadOnly();

        // 深度冻结 MoveExecutionOrder
        IReadOnlyList<string>? frozenOrder = null;
        if (restorePlan.MoveExecutionOrder is not null)
            frozenOrder = Array.AsReadOnly(restorePlan.MoveExecutionOrder.ToArray());

        // 深度冻结 Issues
        var frozenIssues = restorePlan.Issues.ToList().AsReadOnly();

        var frozenPlan = new TargetDisplayLayoutRestorePlan(
            restorePlan.TargetDeviceName,
            restorePlan.SnapshotTargetIconCount,
            restorePlan.AlreadyAtTargetCount,
            restorePlan.MoveRequiredCount,
            restorePlan.MissingCount,
            restorePlan.BlockedCount,
            restorePlan.HasBlockingErrors,
            restorePlan.RequiresTemporaryStaging,
            restorePlan.IsNoOp,
            frozenItems,
            frozenDeps,
            frozenCycles,
            frozenIssues,
            frozenOrder);

        return new TargetDisplayLayoutRecoverySession(
            sessionId: Guid.NewGuid(),
            safetySnapshotId: safetySnapshotId,
            targetDeviceName: restorePlan.TargetDeviceName,
            startedAtUtc: DateTimeOffset.UtcNow,
            sourceRestorePlanItems: frozenItems,
            executionPlanSteps: executionPlan.Steps,
            completedForwardSteps: Array.Empty<int>(),
            completedRollbackSteps: Array.Empty<int>(),
            lastFailedStepIndex: null,
            requiresManualRecovery: false,
            isPositionUncertain: false,
            status: "Ready");
    }

    // ═══════════════════════════════════════════════
    //  逐步执行
    // ═══════════════════════════════════════════════

    private async Task<TargetDisplayLayoutExecutionRunResult> ExecuteStepsAsync(
        TargetDisplayLayoutRecoverySession session,
        TargetDisplayLayoutExecutionPlan executionPlan,
        Guid safetySnapshotId,
        CancellationToken cancellationToken)
    {
        var forwardResults = new List<DesktopIconMoveResult>();
        var stepResults = new List<(int StepIndex, DesktopIconMoveResult Result)>();

        for (var i = 0; i < executionPlan.Steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = executionPlan.Steps[i];

            // 内部一致性
            if (step.StepIndex != i)
                return PositionUncertain(session, i, forwardResults, stepResults, executionPlan, safetySnapshotId,
                    "StepIndexMismatch", $"步骤 #{i} StepIndex={step.StepIndex} 不连续。");

            if (step.FromPosition == step.ToPosition)
                return PositionUncertain(session, i, forwardResults, stepResults, executionPlan, safetySnapshotId,
                    "ZeroLengthStep", $"步骤 #{i} FromPosition == ToPosition。");

            // 调用 Writer
            DesktopIconMoveResult moveResult;
            try
            {
                moveResult = await _staExecutor.RunAsync(
                    () => _positionWriter.MoveDesktopIcon(step.StableId, step.ToPosition))
                    .WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Writer 异常 → 副作用未知，禁止自动回滚
                return PositionUncertain(session, i, forwardResults, stepResults, executionPlan, safetySnapshotId,
                    ex.GetType().Name, $"步骤 #{i} Writer 异常：{ex.Message}");
            }

            forwardResults.Add(moveResult);
            stepResults.Add((i, moveResult));

            // 验证 StableId — 不可信 → PositionUncertain，不自动回滚
            if (!string.Equals(moveResult.StableId, step.StableId, StringComparison.Ordinal))
            {
                return PositionUncertain(session, i, forwardResults, stepResults, executionPlan, safetySnapshotId,
                    "StableIdMismatch",
                    $"步骤 #{i} StableId \"{moveResult.StableId}\" ≠ \"{step.StableId}\"。");
            }

            // 验证 PreviousPosition — 不一致 → PositionUncertain，不自动回滚
            if (moveResult.PreviousPosition != step.FromPosition)
            {
                return PositionUncertain(session, i, forwardResults, stepResults, executionPlan, safetySnapshotId,
                    "PreviousPositionMismatch",
                    $"步骤 #{i} PreviousPosition ({moveResult.PreviousPosition.X},{moveResult.PreviousPosition.Y}) " +
                    $"≠ 预期 ({step.FromPosition.X},{step.FromPosition.Y})。");
            }

            // 验证 ActualPosition — 已知非精确 → 回滚当前步骤
            if (!moveResult.IsExact)
            {
                return await RollbackKnownAsync(
                    session, i, forwardResults, stepResults, executionPlan, safetySnapshotId,
                    "InexactPosition",
                    $"步骤 #{i} ActualPosition ({moveResult.ActualPosition.X},{moveResult.ActualPosition.Y}) " +
                    $"≠ ToPosition ({step.ToPosition.X},{step.ToPosition.Y})。",
                    cancellationToken);
            }
        }

        // ═══ 全部成功 ═══
        var successSession = new TargetDisplayLayoutRecoverySession(
            session.SessionId, session.SafetySnapshotId, session.TargetDeviceName,
            session.StartedAtUtc,
            session.SourceRestorePlanItems, session.ExecutionPlanSteps,
            forwardResults.Select((_, idx) => idx).ToList(),
            Array.Empty<int>(), null, false, false, "Completed");

        return new TargetDisplayLayoutExecutionRunResult(
            TargetDisplayLayoutExecutionRunStatus.Completed,
            true, false, successSession, safetySnapshotId,
            forwardResults, Array.Empty<DesktopIconMoveResult>(),
            null, false, false, null, null);
    }

    // ═══════════════════════════════════════════════
    //  已知副作用回滚
    // ═══════════════════════════════════════════════

    /// <summary>
    /// 只适用于已知 ActualPosition 且能确认 StableId/PreviousPosition 一致的场景。
    /// 先回滚当前非精确步骤，再逆序回滚之前的成功步骤。
    /// </summary>
    private async Task<TargetDisplayLayoutExecutionRunResult> RollbackKnownAsync(
        TargetDisplayLayoutRecoverySession session,
        int failedStepIndex,
        List<DesktopIconMoveResult> forwardResults,
        List<(int StepIndex, DesktopIconMoveResult Result)> stepResults,
        TargetDisplayLayoutExecutionPlan executionPlan,
        Guid safetySnapshotId,
        string exceptionType,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var rollbackResults = new List<DesktopIconMoveResult>();

        // 先回滚当前非精确步骤
        var failedResult = forwardResults[forwardResults.Count - 1];
        var failedStep = executionPlan.Steps[failedStepIndex];
        var currentRollbackTarget = failedStep.FromPosition;
        var currentExpectedPrevious = failedResult.ActualPosition; // 正向结果的实际位置

        DesktopIconMoveResult currentRb;
        try
        {
            currentRb = await _staExecutor.RunAsync(
                () => _positionWriter.MoveDesktopIcon(failedStep.StableId, currentRollbackTarget))
                .WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return PositionUncertain(session, failedStepIndex, forwardResults, stepResults,
                executionPlan, safetySnapshotId, exceptionType, errorMessage,
                rollbackResults);
        }

        rollbackResults.Add(currentRb);

        // 验证当前步骤回滚
        if (!string.Equals(currentRb.StableId, failedStep.StableId, StringComparison.Ordinal)
            || currentRb.PreviousPosition != currentExpectedPrevious)
        {
            return PositionUncertain(session, failedStepIndex, forwardResults, stepResults,
                executionPlan, safetySnapshotId, exceptionType, errorMessage,
                rollbackResults);
        }

        if (!currentRb.IsExact)
        {
            // 已知位置但不精确 — FailedRollbackIncomplete
            return FailedRollbackIncomplete(session, failedStepIndex, forwardResults, rollbackResults,
                stepResults, executionPlan, safetySnapshotId, exceptionType, errorMessage);
        }

        // 当前步骤回滚精确成功，继续逆序回滚之前步骤
        for (var ri = forwardResults.Count - 2; ri >= 0; ri--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (si, _) = stepResults[ri];
            var step = executionPlan.Steps[si];
            var rollbackTarget = step.FromPosition;

            DesktopIconMoveResult rbResult;
            try
            {
                rbResult = await _staExecutor.RunAsync(
                    () => _positionWriter.MoveDesktopIcon(step.StableId, rollbackTarget))
                    .WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                return PositionUncertain(session, failedStepIndex, forwardResults, stepResults,
                    executionPlan, safetySnapshotId, exceptionType, errorMessage,
                    rollbackResults);
            }

            rollbackResults.Add(rbResult);

            if (!string.Equals(rbResult.StableId, step.StableId, StringComparison.Ordinal)
                || rbResult.PreviousPosition != step.ToPosition)
            {
                return PositionUncertain(session, failedStepIndex, forwardResults, stepResults,
                    executionPlan, safetySnapshotId, exceptionType, errorMessage,
                    rollbackResults);
            }

            if (!rbResult.IsExact)
            {
                return FailedRollbackIncomplete(session, failedStepIndex, forwardResults, rollbackResults,
                    stepResults, executionPlan, safetySnapshotId, exceptionType, errorMessage);
            }
        }

        // 全部精确回滚
        var rbSession = new TargetDisplayLayoutRecoverySession(
            session.SessionId, session.SafetySnapshotId, session.TargetDeviceName,
            session.StartedAtUtc,
            session.SourceRestorePlanItems, session.ExecutionPlanSteps,
            forwardResults.Select((_, idx) => idx).ToList(),
            rollbackResults.Select((_, idx) => idx).ToList(),
            failedStepIndex, false, false, "FailedAndRolledBack");

        return new TargetDisplayLayoutExecutionRunResult(
            TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack,
            false, false, rbSession, safetySnapshotId,
            forwardResults, rollbackResults,
            failedStepIndex, false, false,
            exceptionType, errorMessage);
    }

    // ═══════════════════════════════════════════════
    //  安全快照验证
    // ═══════════════════════════════════════════════

    private static string? VerifySafetySnapshot(
        DesktopLayoutSnapshot original, DesktopLayoutSnapshot readBack)
    {
        if (readBack.SnapshotId != original.SnapshotId)
            return $"SnapshotId 不匹配：{readBack.SnapshotId} ≠ {original.SnapshotId}。";
        if (readBack.FormatVersion != original.FormatVersion)
            return $"FormatVersion 不匹配：{readBack.FormatVersion} ≠ {original.FormatVersion}。";
        if (!string.Equals(readBack.TargetMonitorDeviceName, original.TargetMonitorDeviceName, StringComparison.Ordinal))
            return $"TargetMonitorDeviceName 不匹配。";
        if (readBack.ConnectionMethod != original.ConnectionMethod)
            return $"ConnectionMethod 不匹配。";
        if (readBack.Icons.Count != original.Icons.Count)
            return $"Icons.Count 不匹配：{readBack.Icons.Count} ≠ {original.Icons.Count}。";

        for (var i = 0; i < original.Icons.Count; i++)
        {
            var o = original.Icons[i];
            var r = readBack.Icons[i];

            if (r.Ordinal != o.Ordinal) return $"图标 #{i} Ordinal 不匹配。";
            if (!string.Equals(r.StableId, o.StableId, StringComparison.Ordinal))
                return $"图标 #{i} StableId 不匹配。";
            if (!string.Equals(r.DisplayName, o.DisplayName))
                return $"图标 #{i} DisplayName 不匹配。";
            if (!string.Equals(r.ParsingName, o.ParsingName))
                return $"图标 #{i} ParsingName 不匹配。";
            if (r.ItemKind != o.ItemKind) return $"图标 #{i} ItemKind 不匹配。";
            if (r.PixelPosition != o.PixelPosition) return $"图标 #{i} PixelPosition 不匹配。";
            if (!string.Equals(r.MonitorDeviceName, o.MonitorDeviceName, StringComparison.Ordinal))
                return $"图标 #{i} MonitorDeviceName 不匹配。";
            if (r.IsSystemItem != o.IsSystemItem) return $"图标 #{i} IsSystemItem 不匹配。";
            if (r.IsFileSystemItem != o.IsFileSystemItem) return $"图标 #{i} IsFileSystemItem 不匹配。";
        }

        return null;
    }

    // ═══════════════════════════════════════════════
    //  结果工厂
    // ═══════════════════════════════════════════════

    private static TargetDisplayLayoutExecutionRunResult PositionUncertain(
        TargetDisplayLayoutRecoverySession session,
        int failedStepIndex,
        List<DesktopIconMoveResult> forwardResults,
        List<(int, DesktopIconMoveResult)> stepResults,
        TargetDisplayLayoutExecutionPlan executionPlan,
        Guid safetySnapshotId,
        string exceptionType,
        string errorMessage,
        List<DesktopIconMoveResult>? rollbackResults = null)
    {
        var finalRollback = rollbackResults ?? new List<DesktopIconMoveResult>();

        var s = new TargetDisplayLayoutRecoverySession(
            session.SessionId, session.SafetySnapshotId, session.TargetDeviceName,
            session.StartedAtUtc,
            session.SourceRestorePlanItems, session.ExecutionPlanSteps,
            forwardResults.Select((_, idx) => idx).ToList(),
            finalRollback.Select((_, idx) => idx).ToList(),
            failedStepIndex, true, true,
            TargetDisplayLayoutExecutionRunStatus.PositionUncertain.ToString());

        return new TargetDisplayLayoutExecutionRunResult(
            TargetDisplayLayoutExecutionRunStatus.PositionUncertain,
            false, false, s, safetySnapshotId,
            forwardResults, finalRollback,
            failedStepIndex, true, true, exceptionType, errorMessage);
    }

    private static TargetDisplayLayoutExecutionRunResult FailedRollbackIncomplete(
        TargetDisplayLayoutRecoverySession session,
        int failedStepIndex,
        List<DesktopIconMoveResult> forwardResults,
        List<DesktopIconMoveResult> rollbackResults,
        List<(int, DesktopIconMoveResult)> stepResults,
        TargetDisplayLayoutExecutionPlan executionPlan,
        Guid safetySnapshotId,
        string exceptionType,
        string errorMessage)
    {
        var s = new TargetDisplayLayoutRecoverySession(
            session.SessionId, session.SafetySnapshotId, session.TargetDeviceName,
            session.StartedAtUtc,
            session.SourceRestorePlanItems, session.ExecutionPlanSteps,
            forwardResults.Select((_, idx) => idx).ToList(),
            rollbackResults.Select((_, idx) => idx).ToList(),
            failedStepIndex, true, false,
            TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete.ToString());

        return new TargetDisplayLayoutExecutionRunResult(
            TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete,
            false, false, s, safetySnapshotId,
            forwardResults, rollbackResults,
            failedStepIndex, true, false, exceptionType, errorMessage);
    }
}
