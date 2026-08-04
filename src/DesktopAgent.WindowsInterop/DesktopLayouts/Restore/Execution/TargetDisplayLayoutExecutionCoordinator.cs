using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;
using DesktopAgent.WindowsInterop.DesktopIcons;

namespace DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

public sealed class TargetDisplayLayoutExecutionCoordinator
{
    private readonly IDisplayTopologyReader _topologyReader;
    private readonly IDesktopIconReader _iconReader;
    private readonly IDesktopIconPositionWriter _positionWriter;
    private readonly StaThreadExecutor _staExecutor;
    private readonly IDesktopLayoutSafetySnapshotFactory _snapshotFactory;
    private readonly IDesktopLayoutSnapshotStore _snapshotStore;
    private readonly Func<TargetDisplayLayoutRestorePlan, DesktopIconReadResult,
        IReadOnlyList<DisplayDescriptor>, DesktopIconPositioningCapabilities,
        TargetDisplayLayoutExecutionPlan> _createPlan;
    private readonly Func<TargetDisplayLayoutExecutionPlan, TargetDisplayLayoutRecoverySession,
        CancellationToken, Task<TargetDisplayLayoutExecutionVerificationResult>> _verifyForward;
    private readonly Func<TargetDisplayLayoutExecutionPlan, TargetDisplayLayoutRecoverySession,
        CancellationToken, Task<TargetDisplayLayoutExecutionVerificationResult>> _verifyRollback;

    public TargetDisplayLayoutExecutionCoordinator(
        IDisplayTopologyReader topologyReader,
        IDesktopIconReader iconReader,
        IDesktopIconPositionWriter positionWriter,
        StaThreadExecutor staExecutor,
        IDesktopLayoutSafetySnapshotFactory snapshotFactory,
        IDesktopLayoutSnapshotStore snapshotStore)
        : this(topologyReader, iconReader, positionWriter, staExecutor,
              snapshotFactory, snapshotStore,
              TargetDisplayLayoutExecutionPlanner.CreatePlan,
              null, null) // verification delegates set below
    {
    }

    internal TargetDisplayLayoutExecutionCoordinator(
        IDisplayTopologyReader topologyReader,
        IDesktopIconReader iconReader,
        IDesktopIconPositionWriter positionWriter,
        StaThreadExecutor staExecutor,
        IDesktopLayoutSafetySnapshotFactory snapshotFactory,
        IDesktopLayoutSnapshotStore snapshotStore,
        Func<TargetDisplayLayoutRestorePlan, DesktopIconReadResult,
            IReadOnlyList<DisplayDescriptor>, DesktopIconPositioningCapabilities,
            TargetDisplayLayoutExecutionPlan> createPlan,
        Func<TargetDisplayLayoutExecutionPlan, TargetDisplayLayoutRecoverySession,
            CancellationToken, Task<TargetDisplayLayoutExecutionVerificationResult>>? verifyForward = null,
        Func<TargetDisplayLayoutExecutionPlan, TargetDisplayLayoutRecoverySession,
            CancellationToken, Task<TargetDisplayLayoutExecutionVerificationResult>>? verifyRollback = null)
    {
        _topologyReader = topologyReader ?? throw new ArgumentNullException(nameof(topologyReader));
        _iconReader = iconReader ?? throw new ArgumentNullException(nameof(iconReader));
        _positionWriter = positionWriter ?? throw new ArgumentNullException(nameof(positionWriter));
        _staExecutor = staExecutor ?? throw new ArgumentNullException(nameof(staExecutor));
        _snapshotFactory = snapshotFactory ?? throw new ArgumentNullException(nameof(snapshotFactory));
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        _createPlan = createPlan ?? throw new ArgumentNullException(nameof(createPlan));
        _verifyForward = verifyForward ?? VerifyForwardAsync;
        _verifyRollback = verifyRollback ?? VerifyRollbackAsync;
    }

    /// <summary>
    /// 公共入口 — 不执行闸门，保留向后兼容。
    /// </summary>
    public async Task<TargetDisplayLayoutExecutionRunResult> ExecuteAsync(
        TargetDisplayLayoutRestorePlan restorePlan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(restorePlan);
        return await ExecuteCoreAsync(restorePlan, scopeGate: null, cancellationToken);
    }

    /// <summary>
    /// 受控执行入口 — 在最新 ExecutionPlan 创建后、安全快照保存前执行闸门。
    /// scopeGate 为 null 时保持原 public ExecuteAsync 行为。
    /// </summary>
    internal async Task<TargetDisplayLayoutExecutionRunResult> ExecuteWithScopeGateAsync(
        TargetDisplayLayoutRestorePlan restorePlan,
        Func<TargetDisplayLayoutRestorePlan, TargetDisplayLayoutExecutionPlan,
            TargetDisplayLayoutExecutionScopeGate.GateResult> scopeGate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(restorePlan);
        ArgumentNullException.ThrowIfNull(scopeGate);
        return await ExecuteCoreAsync(restorePlan, scopeGate, cancellationToken);
    }

    private async Task<TargetDisplayLayoutExecutionRunResult> ExecuteCoreAsync(
        TargetDisplayLayoutRestorePlan restorePlan,
        Func<TargetDisplayLayoutRestorePlan, TargetDisplayLayoutExecutionPlan,
            TargetDisplayLayoutExecutionScopeGate.GateResult>? scopeGate,
        CancellationToken cancellationToken)
    {
        // ═══ 1. 重新读取 + 分配显示器 ═══
        DesktopIconPositioningCapabilities capabilities;
        DesktopIconReadResult currentRead;
        IReadOnlyList<DisplayDescriptor> currentDisplays;

        try
        {
            capabilities = await _staExecutor.RunAsync(() => _positionWriter.GetCapabilities())
                .WaitAsync(cancellationToken);
            currentDisplays = _topologyReader.GetDisplays();
            var rawRead = await _staExecutor.RunAsync(() => _iconReader.ReadDesktopIcons())
                .WaitAsync(cancellationToken);

            // 为每个图标分配 MonitorDeviceName
            var assignedIcons = rawRead.Icons
                .Select(icon =>
                {
                    var monitor = DisplayAssignmentService.AssignMonitor(icon.PixelPosition, currentDisplays);
                    return icon with { MonitorDeviceName = monitor?.DeviceName };
                })
                .ToList();
            currentRead = new DesktopIconReadResult(
                assignedIcons, rawRead.FailedItemCount,
                rawRead.ConnectionMethod, rawRead.ReadTime,
                rawRead.PrimaryPathException);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return FailedBeforeWriteS(ex.GetType().Name, ex.Message);
        }

        // ═══ 2. 重新生成执行计划 ═══
        TargetDisplayLayoutExecutionPlan executionPlan;
        try
        {
            executionPlan = _createPlan(
                restorePlan, currentRead, currentDisplays, capabilities);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return FailedBeforeWriteS(ex.GetType().Name, $"CreatePlan 异常：{ex.Message}");
        }

        if (!executionPlan.IsExecutable)
            return FailedBeforeWriteS("ExecutionPlanNotExecutable",
                executionPlan.Issues.Count > 0 ? executionPlan.Issues[0].Message : "生成不可执行计划。");

        if (executionPlan.IsNoOp)
            return TargetDisplayLayoutExecutionRunResult.NoOp();

        // ═══ 第一次 Writer 调用前的执行计划结构验证 ═══
        var structureError = ValidateExecutionPlanStructure(executionPlan);
        if (structureError is not null)
            return FailedBeforeWriteS(structureError.Value.ErrorType, structureError.Value.ErrorMessage);

        cancellationToken.ThrowIfCancellationRequested();

        // ═══ 2.5. 受控范围闸门（最新计划审核） ═══
        if (scopeGate is not null)
        {
            TargetDisplayLayoutExecutionScopeGate.GateResult gateResult;
            try
            {
                gateResult = scopeGate(restorePlan, executionPlan);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new TargetDisplayLayoutExecutionRunResult(
                    TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite,
                    false, false, null, null,
                    Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
                    Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
                    null, false, false,
                    "ExecutionScopeGateException",
                    $"scopeGate 异常：{ex.GetType().Name}: {ex.Message}",
                    null, null);
            }

            if (!gateResult.IsAllowed)
            {
                return new TargetDisplayLayoutExecutionRunResult(
                    TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite,
                    false, false, null, null,
                    Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
                    Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
                    null, false, false,
                    "ExecutionScopeRejected",
                    $"执行时闸门拒绝：{string.Join("; ", gateResult.RejectionReasons)}",
                    null, null);
            }
        }

        // ═══ 3-6. 安全快照 ═══
        DesktopLayoutSnapshot safetySnapshot;
        try
        {
            safetySnapshot = _snapshotFactory.Create(
                currentRead.Icons, restorePlan.TargetDeviceName, currentRead.ConnectionMethod);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return FailedBeforeWriteS(ex.GetType().Name, $"SnapshotFactory.Create 异常：{ex.Message}");
        }

        try { await _snapshotStore.SaveAsync(safetySnapshot, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return FailedBeforeWriteS(ex.GetType().Name, $"保存安全快照失败：{ex.Message}");
        }

        DesktopLayoutSnapshot readBack;
        try { readBack = (await _snapshotStore.GetAsync(safetySnapshot.SnapshotId, cancellationToken))!; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new TargetDisplayLayoutExecutionRunResult(
                TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite,
                false, false, null, safetySnapshot.SnapshotId,
                Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
                Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
                null, false, false,
                ex.GetType().Name, $"回读安全快照异常：{ex.Message}", null, null);
        }

        if (readBack is null)
        {
            return new TargetDisplayLayoutExecutionRunResult(
                TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite,
                false, false, null, safetySnapshot.SnapshotId,
                Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
                Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
                null, false, false,
                "SnapshotReadBackNull", "回读安全快照返回 null。", null, null);
        }

        var verifyError = VerifySafetySnapshot(safetySnapshot, readBack);
        if (verifyError is not null)
        {
            return new TargetDisplayLayoutExecutionRunResult(
                TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite,
                false, false, null, safetySnapshot.SnapshotId,
                Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
                Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
                null, false, false,
                "SnapshotVerificationFailed", verifyError, null, null);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // ═══ 7. 创建恢复会话 (含深度冻结) ═══
        var session = new TargetDisplayLayoutRecoverySession(
            sessionId: Guid.NewGuid(),
            safetySnapshotId: safetySnapshot.SnapshotId,
            targetDeviceName: restorePlan.TargetDeviceName,
            startedAtUtc: DateTimeOffset.UtcNow,
            sourceRestorePlan: restorePlan,
            executionPlan: executionPlan,
            completedForwardStepIndexes: Array.Empty<int>(),
            completedRollbackSourceStepIndexes: Array.Empty<int>(),
            lastFailedStepIndex: null,
            requiresManualRecovery: false,
            isPositionUncertain: false,
            status: "Ready");

        // ═══ 8. 逐步执行 ═══
        return await ExecuteStepsAsync(
            session, executionPlan, safetySnapshot.SnapshotId, cancellationToken);
    }

    // ═══════════════════════════════════════════════
    //  逐步执行（Writer 边界不使用 WaitAsync(token)）
    // ═══════════════════════════════════════════════

    private async Task<TargetDisplayLayoutExecutionRunResult> ExecuteStepsAsync(
        TargetDisplayLayoutRecoverySession session,
        TargetDisplayLayoutExecutionPlan executionPlan,
        Guid safetySnapshotId,
        CancellationToken cancellationToken)
    {
        var allAttempts = new List<TargetDisplayLayoutExecutionAttemptResult>();
        var attemptIndex = 0;

        for (var i = 0; i < executionPlan.Steps.Count; i++)
        {
            var step = executionPlan.Steps[i];

            // Writer 开始前检查取消 — 若已有副作用则回滚
            if (cancellationToken.IsCancellationRequested)
            {
                if (allAttempts.Any(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward
                    && a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess))
                {
                    return await RollbackKnownS(
                        session, i - 1, allAttempts, executionPlan, safetySnapshotId, attemptIndex,
                        "OperationCanceledException", "步骤间取消。",
                        null, null, cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
            }

            // 调用 Writer（不取消等待）
            DesktopIconMoveResult moveResult;
            try
            {
                moveResult = await _staExecutor.RunAsync(
                    () => _positionWriter.MoveDesktopIcon(step.StableId, step.ToPosition));
            }
            catch (Exception ex)
            {
                // Writer 异常 → 副作用未知，禁止回滚
                var excAttempt = TargetDisplayLayoutExecutionAttemptResult.FromWriterException(
                    attemptIndex, i, step, ex);
                allAttempts.Add(excAttempt);

                var uncertainSession = BuildSession(session,
                    allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward).ToList(),
                    allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Rollback).ToList(),
                    i, true, true, "PositionUncertain");

                return new TargetDisplayLayoutExecutionRunResult(
                    TargetDisplayLayoutExecutionRunStatus.PositionUncertain,
                    false, false, uncertainSession, safetySnapshotId,
                    allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward).ToList(),
                    allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Rollback).ToList(),
                    i, true, true,
                    ex.GetType().Name, $"步骤 #{i} Writer 异常：{ex.Message}",
                    null, null);
            }

            // Writer 返回了结果 — 记录
            var forwardAttempt = moveResult.IsExact
                ? TargetDisplayLayoutExecutionAttemptResult.FromExactForward(attemptIndex, i, step, moveResult)
                : TargetDisplayLayoutExecutionAttemptResult.FromInexactForward(attemptIndex, i, step, moveResult);

            attemptIndex++;
            allAttempts.Add(forwardAttempt);

            // 验证 StableId
            if (!string.Equals(moveResult.StableId, step.StableId, StringComparison.Ordinal))
            {
                var mismatchAttempt = TargetDisplayLayoutExecutionAttemptResult.FromStableIdMismatch(
                    attemptIndex - 1, i, step, moveResult);
                allAttempts[allAttempts.Count - 1] = mismatchAttempt;

                return PositionUncertainS(session, i, allAttempts, executionPlan, safetySnapshotId,
                    "StableIdMismatch",
                    $"步骤 #{i} StableId \"{moveResult.StableId}\" ≠ \"{step.StableId}\"。",
                    null, null);
            }

            // 验证 PreviousPosition
            if (moveResult.PreviousPosition != step.FromPosition)
            {
                var ppAttempt = TargetDisplayLayoutExecutionAttemptResult.FromPreviousPositionMismatch(
                    attemptIndex - 1, i, step, moveResult);
                allAttempts[allAttempts.Count - 1] = ppAttempt;

                return PositionUncertainS(session, i, allAttempts, executionPlan, safetySnapshotId,
                    "PreviousPositionMismatch",
                    $"步骤 #{i} PreviousPosition ({moveResult.PreviousPosition.X},{moveResult.PreviousPosition.Y}) " +
                    $"≠ 预期 ({step.FromPosition.X},{step.FromPosition.Y})。",
                    null, null);
            }

            // 非精确 → 尝试回滚
            if (!moveResult.IsExact)
            {
                allAttempts[allAttempts.Count - 1] = TargetDisplayLayoutExecutionAttemptResult.FromInexactForward(
                    attemptIndex - 1, i, step, moveResult);

                return await RollbackKnownS(
                    session, i, allAttempts, executionPlan, safetySnapshotId, attemptIndex,
                    "InexactPosition",
                    $"步骤 #{i} ActualPosition ({moveResult.ActualPosition.X},{moveResult.ActualPosition.Y}) " +
                    $"≠ ToPosition ({step.ToPosition.X},{step.ToPosition.Y})。",
                    null, null,
                    cancellationToken);
            }

            // 精确成功 — 检查后续步骤是否应继续
            if (cancellationToken.IsCancellationRequested && i + 1 < executionPlan.Steps.Count)
            {
                // 已完成步骤 i，但还有后续步骤且 token 已取消 → 回滚已知步骤
                return await RollbackKnownS(
                    session, i, allAttempts, executionPlan, safetySnapshotId, attemptIndex,
                    "OperationCanceledException", "Writer 完成后检测到取消。",
                    null, null,
                    cancellationToken);
            }
        }

        // ═══ 全部成功 → 独立最终验证 ═══
        var forwardOnly = allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward).ToList();
        var completedFwd = forwardOnly
            .Where(a => a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
            .Select(a => a.SourceStepIndex).ToList();

        // 使用 CancellationToken.None — Writer 已发生，验证不可被 UI 取消
        var verificationResult = await _verifyForward(
            executionPlan, session, CancellationToken.None);

        var successSession = BuildSession(session, forwardOnly,
            Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            null, false, false, verificationResult.Status == ExecutionVerificationStatus.Passed
                ? "Completed" : "PositionUncertain");
        successSession = new TargetDisplayLayoutRecoverySession(
            successSession.SessionId, successSession.SafetySnapshotId, successSession.TargetDeviceName,
            successSession.StartedAtUtc, successSession.SourceRestorePlan, successSession.ExecutionPlan,
            completedFwd, Array.Empty<int>(),
            null,
            verificationResult.Status != ExecutionVerificationStatus.Passed,
            verificationResult.Status == ExecutionVerificationStatus.Uncertain,
            verificationResult.Status == ExecutionVerificationStatus.Passed
                ? "Completed" : "PositionUncertain");

        if (verificationResult.Status == ExecutionVerificationStatus.Passed)
        {
            return new TargetDisplayLayoutExecutionRunResult(
                TargetDisplayLayoutExecutionRunStatus.Completed, true, false,
                successSession, safetySnapshotId,
                forwardOnly, Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
                null, false, false, null, null, null, null,
                verificationResult);
        }
        else
        {
            return new TargetDisplayLayoutExecutionRunResult(
                TargetDisplayLayoutExecutionRunStatus.PositionUncertain,
                false, false, successSession, safetySnapshotId,
                forwardOnly, Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
                null, true, true,
                "ForwardVerificationFailed",
                $"Forward 独立验证失败 ({verificationResult.Status})：" +
                    string.Join("; ", verificationResult.Issues),
                null, null,
                verificationResult);
        }
    }

    // ═══════════════════════════════════════════════
    //  回滚（不使用 WaitAsync(token)，使用 CancellationToken.None）
    // ═══════════════════════════════════════════════

    private async Task<TargetDisplayLayoutExecutionRunResult> RollbackKnownS(
        TargetDisplayLayoutRecoverySession session,
        int failedStepIndex,
        List<TargetDisplayLayoutExecutionAttemptResult> allAttempts,
        TargetDisplayLayoutExecutionPlan executionPlan,
        Guid safetySnapshotId,
        int attemptIndex,
        string forwardFailureType,
        string forwardFailureMessage,
        string? recoveryFailureType,
        string? recoveryFailureMessage,
        CancellationToken _)
    {
        // Step 1: 回滚当前失败步骤
        var currentAttempt = allAttempts.Last(a =>
            a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward
            && a.SourceStepIndex == failedStepIndex);
        var currentStep = executionPlan.Steps[failedStepIndex];
        var currentRollbackTarget = currentStep.FromPosition;
        var currentExpectedPrevious = currentAttempt.ActualPosition!.Value;

        {
            DesktopIconMoveResult rbResult;
            try
            {
                rbResult = await _staExecutor.RunAsync(
                    () => _positionWriter.MoveDesktopIcon(currentStep.StableId, currentRollbackTarget));
            }
            catch (Exception ex)
            {
                allAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromRollbackException(
                    attemptIndex, failedStepIndex, currentStep, currentRollbackTarget,
                    currentExpectedPrevious, ex));
                return PositionUncertainS(session, failedStepIndex, allAttempts, executionPlan, safetySnapshotId,
                    forwardFailureType, forwardFailureMessage,
                    ex.GetType().Name, $"回滚步骤 #{failedStepIndex} Writer 异常：{ex.Message}");
            }

            // 1. 先验证 StableId
            if (!string.Equals(rbResult.StableId, currentStep.StableId, StringComparison.Ordinal))
            {
                allAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromRollbackStableIdMismatch(
                    attemptIndex, failedStepIndex, currentStep, currentRollbackTarget,
                    currentExpectedPrevious, rbResult));
                return PositionUncertainS(session, failedStepIndex, allAttempts, executionPlan, safetySnapshotId,
                    forwardFailureType, forwardFailureMessage,
                    "RollbackStableIdMismatch",
                    $"回滚步骤 #{failedStepIndex} StableId \"{rbResult.StableId}\" ≠ 期望 \"{currentStep.StableId}\"。");
            }

            // 2. 再验证 PreviousPosition
            if (rbResult.PreviousPosition != currentExpectedPrevious)
            {
                allAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromRollbackPreviousPositionMismatch(
                    attemptIndex, failedStepIndex, currentStep, currentRollbackTarget,
                    currentExpectedPrevious, rbResult));
                return PositionUncertainS(session, failedStepIndex, allAttempts, executionPlan, safetySnapshotId,
                    forwardFailureType, forwardFailureMessage,
                    "RollbackPreviousPositionMismatch",
                    $"回滚步骤 #{failedStepIndex} PreviousPosition ({rbResult.PreviousPosition.X},{rbResult.PreviousPosition.Y}) " +
                    $"≠ 期望 ({currentExpectedPrevious.X},{currentExpectedPrevious.Y})。");
            }

            // 3. 身份可信后判断位置精确性
            if (!rbResult.IsExact)
            {
                allAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromInexactRollback(
                    attemptIndex, failedStepIndex, currentStep, currentRollbackTarget,
                    currentExpectedPrevious, rbResult));
                return FailedRollbackIncompleteS(session, failedStepIndex, allAttempts,
                    executionPlan, safetySnapshotId,
                    forwardFailureType, forwardFailureMessage,
                    "RollbackInexact",
                    $"回滚步骤 #{failedStepIndex} 非精确：Actual=({rbResult.ActualPosition.X},{rbResult.ActualPosition.Y}), " +
                    $"Target=({currentRollbackTarget.X},{currentRollbackTarget.Y})。");
            }

            allAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromExactRollback(
                attemptIndex, failedStepIndex, currentStep, currentRollbackTarget,
                currentExpectedPrevious, rbResult));
            attemptIndex++;
        }

        // Step 2: 逆序回滚之前的精确成功步骤
        var priorSteps = allAttempts
            .Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward
                     && a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess
                     && a.SourceStepIndex != failedStepIndex)
            .OrderByDescending(a => a.SourceStepIndex)
            .ToList();

        foreach (var forwardAttempt in priorSteps)
        {
            var si = forwardAttempt.SourceStepIndex;
            var step = executionPlan.Steps[si];
            var rollbackTarget = step.FromPosition;
            var expectedPrevious = step.ToPosition;

            DesktopIconMoveResult rbResult;
            try
            {
                rbResult = await _staExecutor.RunAsync(
                    () => _positionWriter.MoveDesktopIcon(step.StableId, rollbackTarget));
            }
            catch (Exception ex)
            {
                allAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromRollbackException(
                    attemptIndex, si, step, rollbackTarget, expectedPrevious, ex));
                return PositionUncertainS(session, failedStepIndex, allAttempts, executionPlan, safetySnapshotId,
                    forwardFailureType, forwardFailureMessage,
                    ex.GetType().Name, $"回滚步骤 #{si} Writer 异常：{ex.Message}");
            }

            if (!string.Equals(rbResult.StableId, step.StableId, StringComparison.Ordinal))
            {
                allAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromRollbackStableIdMismatch(
                    attemptIndex, si, step, rollbackTarget, expectedPrevious, rbResult));
                return PositionUncertainS(session, failedStepIndex, allAttempts, executionPlan, safetySnapshotId,
                    forwardFailureType, forwardFailureMessage,
                    "RollbackStableIdMismatch",
                    $"回滚步骤 #{si} StableId \"{rbResult.StableId}\" ≠ 期望 \"{step.StableId}\"。");
            }

            if (rbResult.PreviousPosition != expectedPrevious)
            {
                allAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromRollbackPreviousPositionMismatch(
                    attemptIndex, si, step, rollbackTarget, expectedPrevious, rbResult));
                return PositionUncertainS(session, failedStepIndex, allAttempts, executionPlan, safetySnapshotId,
                    forwardFailureType, forwardFailureMessage,
                    "RollbackPreviousPositionMismatch",
                    $"回滚步骤 #{si} PreviousPosition ({rbResult.PreviousPosition.X},{rbResult.PreviousPosition.Y}) " +
                    $"≠ 期望 ({expectedPrevious.X},{expectedPrevious.Y})。");
            }

            if (!rbResult.IsExact)
            {
                allAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromInexactRollback(
                    attemptIndex, si, step, rollbackTarget, expectedPrevious, rbResult));
                return FailedRollbackIncompleteS(session, failedStepIndex, allAttempts,
                    executionPlan, safetySnapshotId,
                    forwardFailureType, forwardFailureMessage,
                    "RollbackInexact",
                    $"回滚步骤 #{si} 非精确：Actual=({rbResult.ActualPosition.X},{rbResult.ActualPosition.Y}), " +
                    $"Target=({rollbackTarget.X},{rollbackTarget.Y})。");
            }

            allAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromExactRollback(
                attemptIndex, si, step, rollbackTarget, expectedPrevious, rbResult));
            attemptIndex++;
        }

        // 全部精确回滚 → 独立回滚验证（使用 CancellationToken.None，不可取消）
        var allForward = allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward).ToList();
        var allRollback = allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Rollback).ToList();
        var rollbackSourceIndexes = allRollback
            .Where(a => a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
            .Select(a => a.SourceStepIndex).ToList();

        // 回滚后验证使用不可取消 token
        var rbVerification = await _verifyRollback(
            executionPlan, session, CancellationToken.None);

        switch (rbVerification.Status)
        {
            case ExecutionVerificationStatus.Passed:
            {
                var passedSession = BuildSession(session, allForward, allRollback,
                    failedStepIndex, false, false, "FailedAndRolledBack");
                passedSession = new TargetDisplayLayoutRecoverySession(
                    passedSession.SessionId, passedSession.SafetySnapshotId, passedSession.TargetDeviceName,
                    passedSession.StartedAtUtc, passedSession.SourceRestorePlan, passedSession.ExecutionPlan,
                    allForward.Where(a => a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
                        .Select(a => a.SourceStepIndex).ToList(),
                    rollbackSourceIndexes,
                    failedStepIndex, false, false, "FailedAndRolledBack");

                return new TargetDisplayLayoutExecutionRunResult(
                    TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack,
                    false, false, passedSession, safetySnapshotId,
                    allForward, allRollback,
                    failedStepIndex, false, false,
                    forwardFailureType, forwardFailureMessage,
                    recoveryFailureType, recoveryFailureMessage,
                    rbVerification);
            }

            case ExecutionVerificationStatus.Uncertain:
            {
                var uncertainSession = BuildSession(session, allForward, allRollback,
                    failedStepIndex, true, true, "PositionUncertain");
                return new TargetDisplayLayoutExecutionRunResult(
                    TargetDisplayLayoutExecutionRunStatus.PositionUncertain,
                    false, false, uncertainSession, safetySnapshotId,
                    allForward, allRollback,
                    failedStepIndex, true, true,
                    forwardFailureType, forwardFailureMessage,
                    "RollbackVerificationUncertain",
                    $"回滚后独立验证不确定：" + string.Join("; ", rbVerification.Issues),
                    rbVerification);
            }

            default: // Failed
            {
                var failedSession = BuildSession(session, allForward, allRollback,
                    failedStepIndex, true, false, "FailedRollbackIncomplete");
                return new TargetDisplayLayoutExecutionRunResult(
                    TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete,
                    false, false, failedSession, safetySnapshotId,
                    allForward, allRollback,
                    failedStepIndex, true, false,
                    forwardFailureType, forwardFailureMessage,
                    "RollbackVerificationFailed",
                    $"回滚后独立验证失败：" + string.Join("; ", rbVerification.Issues),
                    rbVerification);
            }
        }
    }

    // ═══════════════════════════════════════════════
    //  执行计划结构验证（内部可测试）
    // ═══════════════════════════════════════════════

    /// <summary>验证执行计划结构。返回 null 表示通过，非 null 为错误描述。</summary>
    internal static (string ErrorType, string ErrorMessage)? ValidateExecutionPlanStructure(
        TargetDisplayLayoutExecutionPlan plan)
    {
        if (!plan.IsExecutable)
            return ("PlanNotExecutable", "计划不可执行。");
        if (plan.IsNoOp)
            return null; // NoOp 不需要步骤

        if (plan.Steps.Count == 0)
            return ("EmptySteps", "可执行非 NoOp 计划的 Steps 不能为空。");

        for (var i = 0; i < plan.Steps.Count; i++)
        {
            if (plan.Steps[i].StepIndex != i)
                return ("StepIndexMismatch",
                    $"步骤 #{i} StepIndex={plan.Steps[i].StepIndex}，预期 {i}。");
            if (string.IsNullOrWhiteSpace(plan.Steps[i].StableId))
                return ("BlankStableId",
                    $"步骤 #{i} StableId 为 null 或空白。");
            if (plan.Steps[i].FromPosition == plan.Steps[i].ToPosition)
                return ("ZeroLengthStep",
                    $"步骤 #{i} FromPosition == ToPosition。");
        }

        return null;
    }

    // ═══════════════════════════════════════════════
    //  快照验证
    // ═══════════════════════════════════════════════

    private static string? VerifySafetySnapshot(
        DesktopLayoutSnapshot original, DesktopLayoutSnapshot readBack)
    {
        if (readBack.SnapshotId != original.SnapshotId)
            return $"SnapshotId 不匹配。";
        if (readBack.FormatVersion != original.FormatVersion)
            return $"FormatVersion 不匹配。";
        if (!string.Equals(readBack.TargetMonitorDeviceName, original.TargetMonitorDeviceName, StringComparison.Ordinal))
            return $"TargetMonitorDeviceName 不匹配。";
        if (readBack.ConnectionMethod != original.ConnectionMethod)
            return $"ConnectionMethod 不匹配。";
        if (readBack.Icons.Count != original.Icons.Count)
            return $"Icons.Count 不匹配。";

        for (var i = 0; i < original.Icons.Count; i++)
        {
            var o = original.Icons[i];
            var r = readBack.Icons[i];
            if (r.Ordinal != o.Ordinal) return $"图标 #{i} Ordinal 不匹配。";
            if (!string.Equals(r.StableId, o.StableId, StringComparison.Ordinal)) return $"图标 #{i} StableId 不匹配。";
            if (!string.Equals(r.DisplayName, o.DisplayName)) return $"图标 #{i} DisplayName 不匹配。";
            if (!string.Equals(r.ParsingName, o.ParsingName)) return $"图标 #{i} ParsingName 不匹配。";
            if (r.ItemKind != o.ItemKind) return $"图标 #{i} ItemKind 不匹配。";
            if (r.PixelPosition != o.PixelPosition) return $"图标 #{i} PixelPosition 不匹配。";
            if (!string.Equals(r.MonitorDeviceName, o.MonitorDeviceName, StringComparison.Ordinal)) return $"图标 #{i} MonitorDeviceName 不匹配。";
            if (r.IsSystemItem != o.IsSystemItem) return $"图标 #{i} IsSystemItem 不匹配。";
            if (r.IsFileSystemItem != o.IsFileSystemItem) return $"图标 #{i} IsFileSystemItem 不匹配。";
        }
        return null;
    }

    // ═══════════════════════════════════════════════
    //  结果工厂
    // ═══════════════════════════════════════════════

    private static TargetDisplayLayoutExecutionRunResult FailedBeforeWriteS(
        string type, string msg) =>
        TargetDisplayLayoutExecutionRunResult.FailedBeforeWrite(type, msg);

    private static TargetDisplayLayoutExecutionRunResult PositionUncertainS(
        TargetDisplayLayoutRecoverySession session, int failedStepIndex,
        List<TargetDisplayLayoutExecutionAttemptResult> allAttempts,
        TargetDisplayLayoutExecutionPlan executionPlan, Guid safetySnapshotId,
        string forwardFailureType, string forwardFailureMessage,
        string? recoveryFailureType, string? recoveryFailureMessage)
    {
        var forward = allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward).ToList();
        var rollback = allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Rollback).ToList();

        var s = BuildSession(session, forward, rollback, failedStepIndex, true, true, "PositionUncertain");
        return new TargetDisplayLayoutExecutionRunResult(
            TargetDisplayLayoutExecutionRunStatus.PositionUncertain,
            false, false, s, safetySnapshotId, forward, rollback,
            failedStepIndex, true, true,
            forwardFailureType, forwardFailureMessage,
            recoveryFailureType, recoveryFailureMessage);
    }

    private static TargetDisplayLayoutExecutionRunResult FailedRollbackIncompleteS(
        TargetDisplayLayoutRecoverySession session, int failedStepIndex,
        List<TargetDisplayLayoutExecutionAttemptResult> allAttempts,
        TargetDisplayLayoutExecutionPlan executionPlan, Guid safetySnapshotId,
        string forwardFailureType, string forwardFailureMessage,
        string recoveryFailureType, string recoveryFailureMessage)
    {
        var forward = allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward).ToList();
        var rollback = allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Rollback).ToList();

        var s = BuildSession(session, forward, rollback, failedStepIndex, true, false, "FailedRollbackIncomplete");
        return new TargetDisplayLayoutExecutionRunResult(
            TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete,
            false, false, s, safetySnapshotId, forward, rollback,
            failedStepIndex, true, false,
            forwardFailureType, forwardFailureMessage,
            recoveryFailureType, recoveryFailureMessage);
    }

    private static TargetDisplayLayoutRecoverySession BuildSession(
        TargetDisplayLayoutRecoverySession original,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> forward,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> rollback,
        int? failedStepIndex, bool requiresManual, bool isPositionUncertain, string status) =>
        new(
            original.SessionId, original.SafetySnapshotId, original.TargetDeviceName,
            original.StartedAtUtc, original.SourceRestorePlan, original.ExecutionPlan,
            forward.Where(a => a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
                .Select(a => a.SourceStepIndex).ToList(),
            rollback.Where(a => a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
                .Select(a => a.SourceStepIndex).ToList(),
            failedStepIndex, requiresManual, isPositionUncertain, status);

    // ═══════════════════════════════════════════════
    //  独立最终验证（Forward / Rollback）
    // ═══════════════════════════════════════════════

    private async Task<TargetDisplayLayoutExecutionVerificationResult> VerifyForwardAsync(
        TargetDisplayLayoutExecutionPlan executionPlan,
        TargetDisplayLayoutRecoverySession session,
        CancellationToken cancellationToken)
    {
        var verifiedAt = DateTimeOffset.UtcNow;
        var targetDeviceName = executionPlan.TargetDeviceName;

        // 执行完整桌面重读
        DesktopIconReadResult currentRead;
        IReadOnlyList<DisplayDescriptor> currentDisplays;
        try
        {
            currentDisplays = _topologyReader.GetDisplays();
            var rawRead = await _staExecutor.RunAsync(() => _iconReader.ReadDesktopIcons());
            // 不使用 WaitAsync(token) — Writer 已发生，必须完成验证
            var assignedIcons = rawRead.Icons.Select(icon =>
            {
                var monitor = DisplayAssignmentService.AssignMonitor(icon.PixelPosition, currentDisplays);
                return icon with { MonitorDeviceName = monitor?.DeviceName };
            }).ToList();
            currentRead = new DesktopIconReadResult(
                assignedIcons, rawRead.FailedItemCount,
                rawRead.ConnectionMethod, rawRead.ReadTime,
                rawRead.PrimaryPathException);
        }
        catch (Exception ex)
        {
            return TargetDisplayLayoutExecutionVerificationResult.ForwardUncertain(
                0, new[] { $"Forward 验证读取异常：{ex.GetType().Name}: {ex.Message}" }, verifiedAt);
        }

        var issues = new List<string>();

        // FailedItemCount 检查
        if (currentRead.FailedItemCount != 0)
        {
            issues.Add($"FailedItemCount = {currentRead.FailedItemCount}，读取不完整。");
            return TargetDisplayLayoutExecutionVerificationResult.ForwardUncertain(
                currentRead.FailedItemCount, issues, verifiedAt);
        }

        // StableId 唯一性
        var sidSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var icon in currentRead.Icons)
        {
            if (!sidSet.Add(icon.StableId))
                issues.Add($"StableId \"{icon.StableId}\" 重复。");
        }
        if (issues.Count > 0)
            return TargetDisplayLayoutExecutionVerificationResult.ForwardUncertain(
                0, issues, verifiedAt);

        // 建立当前 StableId → 图标 映射
        var currentBySid = new Dictionary<string, DesktopIconDescriptor>(StringComparer.Ordinal);
        foreach (var icon in currentRead.Icons)
            currentBySid[icon.StableId] = icon;

        // 验证 RestorePlan 中应在目标显示器的所有目标项
        var missingCount = 0;
        foreach (var item in session.SourceRestorePlan.Items)
        {
            if (item.State is TargetDisplayLayoutRestoreItemState.MissingFromCurrent)
                continue;

            if (!currentBySid.TryGetValue(item.StableId, out var currentIcon))
            {
                issues.Add($"StableId \"{item.StableId}\" 在 Forward 验证读取中缺失。");
                missingCount++;
                continue;
            }

            // 已在目标显示器上 — 验证位置
            if (string.Equals(currentIcon.MonitorDeviceName ?? "", targetDeviceName, StringComparison.Ordinal))
            {
                if (item.State == TargetDisplayLayoutRestoreItemState.MoveRequired)
                {
                    if (currentIcon.PixelPosition != item.SnapshotTargetPosition)
                    {
                        issues.Add($"\"{item.StableId}\" Forward 验证位置 " +
                            $"({currentIcon.PixelPosition.X},{currentIcon.PixelPosition.Y}) ≠ " +
                            $"目标 ({item.SnapshotTargetPosition.X},{item.SnapshotTargetPosition.Y})。");
                    }
                }

                if (item.State == TargetDisplayLayoutRestoreItemState.AlreadyAtTarget)
                {
                    if (currentIcon.PixelPosition != item.SnapshotTargetPosition)
                    {
                        issues.Add($"\"{item.StableId}\" (AlreadyAtTarget) Forward 验证位置 " +
                            $"({currentIcon.PixelPosition.X},{currentIcon.PixelPosition.Y}) ≠ " +
                            $"快照目标 ({item.SnapshotTargetPosition.X},{item.SnapshotTargetPosition.Y})。");
                    }
                }
            }
            else
            {
                // 不在目标显示器上 — 报告显示器不一致
                if (item.State == TargetDisplayLayoutRestoreItemState.MoveRequired
                    || item.State == TargetDisplayLayoutRestoreItemState.AlreadyAtTarget)
                {
                    issues.Add($"\"{item.StableId}\" Forward 验证显示器 " +
                        $"\"{currentIcon.MonitorDeviceName}\" ≠ 目标 \"{targetDeviceName}\"。");
                }
                // AlreadyAtTarget 不在目标显示器上时仍验证位置（应与快照目标一致）
                if (item.State == TargetDisplayLayoutRestoreItemState.AlreadyAtTarget)
                {
                    if (currentIcon.PixelPosition != item.SnapshotTargetPosition)
                    {
                        issues.Add($"\"{item.StableId}\" (AlreadyAtTarget) Forward 验证位置 " +
                            $"({currentIcon.PixelPosition.X},{currentIcon.PixelPosition.Y}) ≠ " +
                            $"快照目标 ({item.SnapshotTargetPosition.X},{item.SnapshotTargetPosition.Y})。");
                    }
                }
            }
        }

        if (missingCount > 0)
            return TargetDisplayLayoutExecutionVerificationResult.ForwardUncertain(
                missingCount, issues, verifiedAt);

        if (issues.Count > 0)
            return TargetDisplayLayoutExecutionVerificationResult.ForwardFailed(
                0, issues, verifiedAt);

        return TargetDisplayLayoutExecutionVerificationResult.ForwardPassed(verifiedAt);
    }

    private async Task<TargetDisplayLayoutExecutionVerificationResult> VerifyRollbackAsync(
        TargetDisplayLayoutExecutionPlan executionPlan,
        TargetDisplayLayoutRecoverySession session,
        CancellationToken cancellationToken)
    {
        var verifiedAt = DateTimeOffset.UtcNow;

        // 收集所有被回滚步骤的原始 FromPosition（即回滚恢复目标）
        var rollbackTargets = new Dictionary<string, PixelPoint>(StringComparer.Ordinal);
        foreach (var step in executionPlan.Steps)
        {
            var fromPos = step.FromPosition;
            if (!rollbackTargets.ContainsKey(step.StableId))
                rollbackTargets[step.StableId] = fromPos;
        }

        DesktopIconReadResult currentRead;
        try
        {
            var currentDisplays = _topologyReader.GetDisplays();
            var rawRead = await _staExecutor.RunAsync(() => _iconReader.ReadDesktopIcons());
            var assignedIcons = rawRead.Icons.Select(icon =>
            {
                var monitor = DisplayAssignmentService.AssignMonitor(icon.PixelPosition, currentDisplays);
                return icon with { MonitorDeviceName = monitor?.DeviceName };
            }).ToList();
            currentRead = new DesktopIconReadResult(
                assignedIcons, rawRead.FailedItemCount,
                rawRead.ConnectionMethod, rawRead.ReadTime,
                rawRead.PrimaryPathException);
        }
        catch (Exception ex)
        {
            return TargetDisplayLayoutExecutionVerificationResult.RollbackUncertain(
                0, new[] { $"Rollback 验证读取异常：{ex.GetType().Name}: {ex.Message}" }, verifiedAt);
        }

        var issues = new List<string>();

        if (currentRead.FailedItemCount != 0)
        {
            issues.Add($"FailedItemCount = {currentRead.FailedItemCount}，读取不完整。");
            return TargetDisplayLayoutExecutionVerificationResult.RollbackUncertain(
                currentRead.FailedItemCount, issues, verifiedAt);
        }

        var sidSet = new HashSet<string>(StringComparer.Ordinal);
        var currentBySid = new Dictionary<string, DesktopIconDescriptor>(StringComparer.Ordinal);
        foreach (var icon in currentRead.Icons)
        {
            if (!sidSet.Add(icon.StableId))
                issues.Add($"StableId \"{icon.StableId}\" 重复。");
            currentBySid[icon.StableId] = icon;
        }
        if (issues.Count > 0)
            return TargetDisplayLayoutExecutionVerificationResult.RollbackUncertain(
                0, issues, verifiedAt);

        // 验证每个被回滚的图标
        var notRestored = 0;
        foreach (var (sid, expectedPos) in rollbackTargets)
        {
            if (!currentBySid.TryGetValue(sid, out var currentIcon))
            {
                issues.Add($"StableId \"{sid}\" 在 Rollback 验证读取中缺失。");
                return TargetDisplayLayoutExecutionVerificationResult.RollbackUncertain(
                    0, issues, verifiedAt);
            }

            if (currentIcon.PixelPosition != expectedPos)
            {
                issues.Add($"\"{sid}\" Rollback 验证位置 " +
                    $"({currentIcon.PixelPosition.X},{currentIcon.PixelPosition.Y}) ≠ " +
                    $"原始 FromPosition ({expectedPos.X},{expectedPos.Y})。");
                notRestored++;
            }

            // 验证回滚后显示器归属与原始状态一致
            var targetDeviceName = session.TargetDeviceName;
            if (!string.Equals(currentIcon.MonitorDeviceName ?? "", targetDeviceName, StringComparison.Ordinal))
            {
                // 回滚后图标可能在其他显示器上 — 记录但不算位置错误
                // 除非原始图标就在目标显示器上且有明确的 CurrentMonitorDeviceName
                foreach (var item in session.SourceRestorePlan.Items)
                {
                    if (string.Equals(item.StableId, sid, StringComparison.Ordinal)
                        && item.CurrentMonitorDeviceName is not null
                        && !string.Equals(currentIcon.MonitorDeviceName ?? "", item.CurrentMonitorDeviceName, StringComparison.Ordinal))
                    {
                        issues.Add($"\"{sid}\" Rollback 验证显示器 \"{currentIcon.MonitorDeviceName}\" ≠ " +
                            $"原始 \"{item.CurrentMonitorDeviceName}\"。");
                    }
                }
            }
        }

        if (notRestored > 0)
            return TargetDisplayLayoutExecutionVerificationResult.RollbackFailed(
                notRestored, issues, verifiedAt);

        if (issues.Count > 0)
            return TargetDisplayLayoutExecutionVerificationResult.RollbackUncertain(
                0, issues, verifiedAt);

        return TargetDisplayLayoutExecutionVerificationResult.RollbackPassed(verifiedAt);
    }
}
