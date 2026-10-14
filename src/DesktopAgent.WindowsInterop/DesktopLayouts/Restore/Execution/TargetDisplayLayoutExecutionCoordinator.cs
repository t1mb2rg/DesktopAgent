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
    private readonly IRecoverySessionStore? _recoveryStore;
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
        IDesktopLayoutSnapshotStore snapshotStore,
        IRecoverySessionStore? recoveryStore = null)
        : this(topologyReader, iconReader, positionWriter, staExecutor,
              snapshotFactory, snapshotStore,
              TargetDisplayLayoutExecutionPlanner.CreatePlan,
              null, null,
              recoveryStore)
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
            CancellationToken, Task<TargetDisplayLayoutExecutionVerificationResult>>? verifyRollback = null,
        IRecoverySessionStore? recoveryStore = null)
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
        _recoveryStore = recoveryStore;
    }

    /// <summary>
    /// 公共入口 — 不执行闸门，保留向后兼容。
    /// </summary>
    public async Task<TargetDisplayLayoutExecutionRunResult> ExecuteAsync(
        TargetDisplayLayoutRestorePlan restorePlan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(restorePlan);
        return await ExecuteCoreAsync(
            restorePlan, scopeGate: null, cancellationToken, RecoverySessionOrigin.Organize);
    }

    /// <summary>
    /// 受控执行入口 — 在最新 ExecutionPlan 创建后、安全快照保存前执行闸门。
    /// scopeGate 为 null 时保持原 public ExecuteAsync 行为。
    ///
    /// <paramref name="origin"/> 会写进恢复会话行：撤销入口按它区分「用户的整理」与
    /// 「撤销自己写下的还原」，否则第二次点撤销会把桌面重新整理一遍。
    /// </summary>
    internal async Task<TargetDisplayLayoutExecutionRunResult> ExecuteWithScopeGateAsync(
        TargetDisplayLayoutRestorePlan restorePlan,
        Func<TargetDisplayLayoutRestorePlan, TargetDisplayLayoutExecutionPlan,
            TargetDisplayLayoutExecutionScopeGate.GateResult> scopeGate,
        CancellationToken cancellationToken = default,
        RecoverySessionOrigin origin = RecoverySessionOrigin.Organize)
    {
        ArgumentNullException.ThrowIfNull(restorePlan);
        ArgumentNullException.ThrowIfNull(scopeGate);
        return await ExecuteCoreAsync(restorePlan, scopeGate, cancellationToken, origin);
    }

    private async Task<TargetDisplayLayoutExecutionRunResult> ExecuteCoreAsync(
        TargetDisplayLayoutRestorePlan restorePlan,
        Func<TargetDisplayLayoutRestorePlan, TargetDisplayLayoutExecutionPlan,
            TargetDisplayLayoutExecutionScopeGate.GateResult>? scopeGate,
        CancellationToken cancellationToken,
        RecoverySessionOrigin origin)
    {
        TargetDisplayLayoutExecutionPlan executionPlan;
        DesktopLayoutSnapshot safetySnapshot;

        // ═══ Pre-Writer: OCE → FailedBeforeWrite ═══
        try
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
        }
        catch (OperationCanceledException ex)
        {
            // Writer 未启动前取消 → FailedBeforeWrite，不进入人工处理锁
            return FailedBeforeWriteS("OperationCanceledBeforeWrite",
                $"Writer 未启动即取消：{ex.GetType().Name}: {ex.Message}");
        }

        // ═══ 7. 创建恢复会话 (含深度冻结) ═══
        // 在 try-catch 外 — 仅使用已校验的非 null 变量，无取消风险
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
            status: "Ready",
            origin: origin);

        // ═══ 7.5. 初始持久化检查点（第一笔 Writer 前） ═══
        RecoverySessionRecord? checkpoint = null;
        if (_recoveryStore is not null)
        {
            var (ok, record) = await TrySaveInitialCheckpointAsync(
                session, executionPlan, safetySnapshot.SnapshotId);
            if (!ok)
            {
                return FailedBeforeWriteS("InitialCheckpointFailed",
                    "初始恢复会话持久化失败：Save 或 readback 不通过。Writer 未启动。");
            }
            checkpoint = record;
        }

        // ═══ 8. 逐步执行 ═══
        return await ExecuteStepsAsync(
            session, executionPlan, safetySnapshot.SnapshotId, cancellationToken, checkpoint);
    }

    // ═══════════════════════════════════════════════
    //  逐步执行（Writer 边界不使用 WaitAsync(token)）
    // ═══════════════════════════════════════════════

    private async Task<TargetDisplayLayoutExecutionRunResult> ExecuteStepsAsync(
        TargetDisplayLayoutRecoverySession session,
        TargetDisplayLayoutExecutionPlan executionPlan,
        Guid safetySnapshotId,
        CancellationToken cancellationToken,
        RecoverySessionRecord? currentCheckpoint = null)
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
                        null, null, cancellationToken, currentCheckpoint);
                }
                return FailedBeforeWriteS("OperationCanceledBeforeWrite",
                    "Writer 未启动即取消。");
            }

            DesktopIconMoveResult moveResult;
            try
            {
                moveResult = await _staExecutor.RunAsync(
                    () => _positionWriter.MoveDesktopIcon(step.StableId, step.ToPosition));
            }
            catch (Exception ex)
            {
                var excAttempt = TargetDisplayLayoutExecutionAttemptResult.FromWriterException(
                    attemptIndex, i, step, ex);
                allAttempts.Add(excAttempt);
                var fwdOnly = allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward).ToList();
                var rbOnly = allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Rollback).ToList();
                var exceptionCompletedFwd = fwdOnly
                    .Where(a => a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
                    .Select(a => a.SourceStepIndex).Distinct().OrderBy(x => x).ToList();

                if (currentCheckpoint is not null)
                {
                    var (persisted, _) = await TryUpdateAndGetVerifiedAsync(session, fwdOnly, rbOnly,
                        exceptionCompletedFwd, Array.Empty<int>(),
                        RecoverySessionLifecycle.PositionUncertain, true, true, i,
                        ex.GetType().Name, $"步骤 #{i} Writer 异常：{ex.Message}",
                        null, null, null, currentCheckpoint);
                    if (!persisted)
                    {
                        return PositionUncertainS(session, i, allAttempts,
                            executionPlan, safetySnapshotId,
                            "TerminalPersistFailed",
                            "WriterException 发生后，PositionUncertain 终态持久化失败。",
                            null, null);
                    }
                }

                var uncertainSession = BuildSession(session, fwdOnly, rbOnly,
                    i, true, true, "PositionUncertain");
                return new TargetDisplayLayoutExecutionRunResult(
                    TargetDisplayLayoutExecutionRunStatus.PositionUncertain,
                    false, false, uncertainSession, safetySnapshotId, fwdOnly, rbOnly,
                    i, true, true,
                    ex.GetType().Name, $"步骤 #{i} Writer 异常：{ex.Message}",
                    null, null);
            }

            var forwardAttempt = moveResult.IsExact
                ? TargetDisplayLayoutExecutionAttemptResult.FromExactForward(attemptIndex, i, step, moveResult)
                : TargetDisplayLayoutExecutionAttemptResult.FromInexactForward(attemptIndex, i, step, moveResult);

            attemptIndex++;
            allAttempts.Add(forwardAttempt);

            if (!string.Equals(moveResult.StableId, step.StableId, StringComparison.Ordinal))
            {
                var mismatchAttempt = TargetDisplayLayoutExecutionAttemptResult.FromStableIdMismatch(
                    attemptIndex - 1, i, step, moveResult);
                allAttempts[allAttempts.Count - 1] = mismatchAttempt;

                if (currentCheckpoint is not null)
                {
                    var mismatchFwdOnly = allAttempts
                        .Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward).ToList();
                    var mismatchRbOnly = allAttempts
                        .Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Rollback).ToList();
                    var mismatchCompletedFwd = mismatchFwdOnly
                        .Where(a => a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
                        .Select(a => a.SourceStepIndex).Distinct().OrderBy(x => x).ToList();
                    var (persisted, _) = await TryUpdateAndGetVerifiedAsync(session,
                        mismatchFwdOnly, mismatchRbOnly,
                        mismatchCompletedFwd, Array.Empty<int>(),
                        RecoverySessionLifecycle.PositionUncertain, true, true, i,
                        "StableIdMismatch",
                        $"步骤 #{i} StableId \"{moveResult.StableId}\" ≠ \"{step.StableId}\"。",
                        null, null, null, currentCheckpoint);
                    if (!persisted)
                    {
                        return PositionUncertainS(session, i, allAttempts,
                            executionPlan, safetySnapshotId,
                            "TerminalPersistFailed",
                            "StableIdMismatch 发生后，PositionUncertain 终态持久化失败。",
                            null, null);
                    }
                }

                return PositionUncertainS(session, i, allAttempts, executionPlan, safetySnapshotId,
                    "StableIdMismatch",
                    $"步骤 #{i} StableId \"{moveResult.StableId}\" ≠ \"{step.StableId}\"。",
                    null, null);
            }

            if (moveResult.PreviousPosition != step.FromPosition)
            {
                var ppAttempt = TargetDisplayLayoutExecutionAttemptResult.FromPreviousPositionMismatch(
                    attemptIndex - 1, i, step, moveResult);
                allAttempts[allAttempts.Count - 1] = ppAttempt;

                if (currentCheckpoint is not null)
                {
                    var mismatchFwdOnly = allAttempts
                        .Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward).ToList();
                    var mismatchRbOnly = allAttempts
                        .Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Rollback).ToList();
                    var mismatchCompletedFwd = mismatchFwdOnly
                        .Where(a => a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
                        .Select(a => a.SourceStepIndex).Distinct().OrderBy(x => x).ToList();
                    var (persisted, _) = await TryUpdateAndGetVerifiedAsync(session,
                        mismatchFwdOnly, mismatchRbOnly,
                        mismatchCompletedFwd, Array.Empty<int>(),
                        RecoverySessionLifecycle.PositionUncertain, true, true, i,
                        "PreviousPositionMismatch",
                        $"步骤 #{i} PreviousPosition mismatch。",
                        null, null, null, currentCheckpoint);
                    if (!persisted)
                    {
                        return PositionUncertainS(session, i, allAttempts,
                            executionPlan, safetySnapshotId,
                            "TerminalPersistFailed",
                            "PreviousPositionMismatch 发生后，PositionUncertain 终态持久化失败。",
                            null, null);
                    }
                }

                return PositionUncertainS(session, i, allAttempts, executionPlan, safetySnapshotId,
                    "PreviousPositionMismatch",
                    $"步骤 #{i} PreviousPosition ({moveResult.PreviousPosition.X},{moveResult.PreviousPosition.Y}) " +
                    $"≠ 预期 ({step.FromPosition.X},{step.FromPosition.Y})。",
                    null, null);
            }

            if (!moveResult.IsExact)
            {
                allAttempts[allAttempts.Count - 1] = TargetDisplayLayoutExecutionAttemptResult.FromInexactForward(
                    attemptIndex - 1, i, step, moveResult);

                if (currentCheckpoint is not null)
                {
                    var inexactFwdOnly = allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward).ToList();
                    var inexactRbOnly = allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Rollback).ToList();
                    var inexactCompletedFwd = inexactFwdOnly
                        .Where(a => a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
                        .Select(a => a.SourceStepIndex).ToList();

                    var (persisted, _) = await TryUpdateAndGetVerifiedAsync(session,
                        inexactFwdOnly, inexactRbOnly,
                        inexactCompletedFwd, Array.Empty<int>(),
                        RecoverySessionLifecycle.PositionUncertain, true, true, i,
                        "InexactPosition",
                        $"步骤 #{i} ActualPosition ({moveResult.ActualPosition.X},{moveResult.ActualPosition.Y}) " +
                        $"≠ ToPosition ({step.ToPosition.X},{step.ToPosition.Y})。",
                        null, null, null, currentCheckpoint);
                    if (!persisted)
                    {
                        return PositionUncertainS(session, i, allAttempts,
                            executionPlan, safetySnapshotId,
                            "TerminalPersistFailed",
                            "InexactPosition 发生后，PositionUncertain 终态持久化失败。",
                            null, null);
                    }

                    return PositionUncertainS(session, i, allAttempts,
                        executionPlan, safetySnapshotId,
                        "InexactPosition",
                        $"步骤 #{i} ActualPosition ({moveResult.ActualPosition.X},{moveResult.ActualPosition.Y}) " +
                        $"≠ ToPosition ({step.ToPosition.X},{step.ToPosition.Y})。",
                        null, null);
                }

                return await RollbackKnownS(
                    session, i, allAttempts, executionPlan, safetySnapshotId, attemptIndex,
                    "InexactPosition",
                    $"步骤 #{i} ActualPosition ({moveResult.ActualPosition.X},{moveResult.ActualPosition.Y}) " +
                    $"≠ ToPosition ({step.ToPosition.X},{step.ToPosition.Y})。",
                    null, null,
                    cancellationToken);
            }

            if (currentCheckpoint is not null)
            {
                var fwdOnly = allAttempts
                    .Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward).ToList();
                var exactFwdIndexes = fwdOnly
                    .Where(a => a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
                    .Select(a => a.SourceStepIndex).OrderBy(x => x).ToList();
                (bool ok, RecoverySessionRecord? newCp) = await TryUpdateAndGetVerifiedAsync(
                    session, fwdOnly, Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
                    exactFwdIndexes, Array.Empty<int>(),
                    RecoverySessionLifecycle.Executing,
                    false, false, null,
                    null, null, null, null, null,
                    currentCheckpoint);
                if (!ok)
                {
                    return PositionUncertainS(session, i, allAttempts,
                        executionPlan, safetySnapshotId,
                        "CheckpointPersistFailed",
                        "持久化检查点失败 — Writer 已执行但无法可靠记录。",
                        null, null);
                }
                currentCheckpoint = newCp;
            }

            if (cancellationToken.IsCancellationRequested && i + 1 < executionPlan.Steps.Count)
            {
                return await RollbackKnownS(
                    session, i, allAttempts, executionPlan, safetySnapshotId, attemptIndex,
                    "OperationCanceledException", "Writer 完成后检测到取消。",
                    null, null,
                    cancellationToken, currentCheckpoint);
            }
        }

        var forwardOnly = allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward).ToList();
        var completedFwd = forwardOnly
            .Where(a => a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
            .Select(a => a.SourceStepIndex).ToList();

        TargetDisplayLayoutExecutionVerificationResult verificationResult;
        try
        {
            verificationResult = await _verifyForward(
                executionPlan, session, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var issue =
                $"Forward 验证抛出异常：{ex.GetType().Name}: {ex.Message}";
            var uncertainVerification =
                TargetDisplayLayoutExecutionVerificationResult.ForwardUncertain(
                    0, new[] { issue }, DateTimeOffset.UtcNow);

            var veCompletedFwd = forwardOnly
                .Where(a => a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
                .Select(a => a.SourceStepIndex).ToList();
            var persistOk = true;
            if (currentCheckpoint is not null)
            {
                (persistOk, _) = await TryUpdateAndGetVerifiedAsync(
                    session, forwardOnly,
                    Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
                    veCompletedFwd, Array.Empty<int>(),
                    RecoverySessionLifecycle.PositionUncertain, true, true, null,
                    "ForwardVerificationException", issue,
                    null, null,
                    uncertainVerification,
                    currentCheckpoint);
            }

            if (!persistOk)
            {
                return new TargetDisplayLayoutExecutionRunResult(
                    TargetDisplayLayoutExecutionRunStatus.PositionUncertain,
                    false, false, null, safetySnapshotId,
                    forwardOnly, Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
                    null, true, true,
                    "TerminalPersistFailed",
                    "ForwardVerificationException 发生后，终态持久化失败。",
                    null, null,
                    uncertainVerification);
            }

            var veSession = BuildSession(session, forwardOnly,
                Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
                null, true, true, "PositionUncertain");
            return new TargetDisplayLayoutExecutionRunResult(
                TargetDisplayLayoutExecutionRunStatus.PositionUncertain,
                false, false, veSession, safetySnapshotId,
                forwardOnly, Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
                null, true, true,
                "ForwardVerificationException",
                issue,
                null, null,
                uncertainVerification);
        }

        var nonPassed = verificationResult.Status != ExecutionVerificationStatus.Passed;
        var successSession = BuildSession(session, forwardOnly,
            Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
            null, nonPassed, nonPassed, nonPassed
                ? "PositionUncertain" : "Completed");
        successSession = new TargetDisplayLayoutRecoverySession(
            successSession.SessionId, successSession.SafetySnapshotId, successSession.TargetDeviceName,
            successSession.StartedAtUtc, successSession.SourceRestorePlan, successSession.ExecutionPlan,
            completedFwd, Array.Empty<int>(),
            null,
            nonPassed, nonPassed,
            nonPassed ? "PositionUncertain" : "Completed",
            origin: successSession.Origin);

        var terminalPersisted = true;
        if (_recoveryStore is not null && currentCheckpoint is not null)
        {
            var finalLifecycle = verificationResult.Status == ExecutionVerificationStatus.Passed
                ? RecoverySessionLifecycle.Completed
                : RecoverySessionLifecycle.PositionUncertain;
            var (ok, _) = await TryUpdateAndGetVerifiedAsync(
                session, forwardOnly,
                Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
                completedFwd, Array.Empty<int>(),
                finalLifecycle,
                nonPassed, nonPassed, null,
                nonPassed ? "ForwardVerificationFailed" : null,
                nonPassed ? $"Forward 独立验证失败 ({verificationResult.Status})" : null,
                null, null,
                verificationResult,
                currentCheckpoint);
            terminalPersisted = ok;
        }

        if (_recoveryStore is not null && currentCheckpoint is not null && !terminalPersisted)
        {
            return new TargetDisplayLayoutExecutionRunResult(
                TargetDisplayLayoutExecutionRunStatus.PositionUncertain,
                false, false, successSession, safetySnapshotId,
                forwardOnly, Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
                null, true, true,
                "TerminalPersistFailed",
                $"Forward 验证{(verificationResult.Status == ExecutionVerificationStatus.Passed ? "通过" : "未通过")}但终态持久化失败。",
                null, null,
                verificationResult);
        }

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
        CancellationToken _,
        RecoverySessionRecord? currentCheckpoint = null)
    {
        if (currentCheckpoint is null)
            return await RollbackKnownS_Legacy(session, failedStepIndex, allAttempts,
                executionPlan, safetySnapshotId, attemptIndex,
                forwardFailureType, forwardFailureMessage, recoveryFailureType, recoveryFailureMessage);

        var candidates = allAttempts
            .Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward
                     && a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
            .GroupBy(a => a.SourceStepIndex)
            .Select(g => g.First())
            .OrderByDescending(a => a.SourceStepIndex)
            .ToList();

        var rbSourceIndexes = new List<int>();

        foreach (var candidate in candidates)
        {
            var si = candidate.SourceStepIndex;
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
                if (currentCheckpoint is not null)
                {
                    var (ok, _) = await PersistRollbackCheckpointAsync(
                        session, allAttempts, rbSourceIndexes,
                        RecoverySessionLifecycle.PositionUncertain, true, true, failedStepIndex,
                        forwardFailureType, forwardFailureMessage,
                        "RollbackWriterException", $"回滚步骤 #{si} Writer 异常：{ex.Message}",
                        currentCheckpoint);
                    if (!ok)
                    {
                        return PositionUncertainS(session, failedStepIndex, allAttempts,
                            executionPlan, safetySnapshotId,
                            forwardFailureType, forwardFailureMessage,
                            "TerminalPersistFailed",
                            "RollbackWriterException 后终态持久化失败。");
                    }
                }
                return PositionUncertainS(session, failedStepIndex, allAttempts,
                    executionPlan, safetySnapshotId,
                    forwardFailureType, forwardFailureMessage,
                    "RollbackWriterException", $"回滚步骤 #{si} Writer 异常：{ex.Message}");
            }

            if (!string.Equals(rbResult.StableId, step.StableId, StringComparison.Ordinal))
            {
                allAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromRollbackStableIdMismatch(
                    attemptIndex, si, step, rollbackTarget, expectedPrevious, rbResult));
                if (currentCheckpoint is not null)
                {
                    var (ok, _) = await PersistRollbackCheckpointAsync(
                        session, allAttempts, rbSourceIndexes,
                        RecoverySessionLifecycle.PositionUncertain, true, true, failedStepIndex,
                        forwardFailureType, forwardFailureMessage,
                        "RollbackStableIdMismatch",
                        $"回滚步骤 #{si} StableId \"{rbResult.StableId}\" ≠ 期望 \"{step.StableId}\"。",
                        currentCheckpoint);
                    if (!ok)
                    {
                        return PositionUncertainS(session, failedStepIndex, allAttempts,
                            executionPlan, safetySnapshotId,
                            forwardFailureType, forwardFailureMessage,
                            "TerminalPersistFailed",
                            "RollbackStableIdMismatch 后终态持久化失败。");
                    }
                }
                return PositionUncertainS(session, failedStepIndex, allAttempts,
                    executionPlan, safetySnapshotId,
                    forwardFailureType, forwardFailureMessage,
                    "RollbackStableIdMismatch",
                    $"回滚步骤 #{si} StableId \"{rbResult.StableId}\" ≠ 期望 \"{step.StableId}\"。");
            }

            if (rbResult.PreviousPosition != expectedPrevious)
            {
                allAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromRollbackPreviousPositionMismatch(
                    attemptIndex, si, step, rollbackTarget, expectedPrevious, rbResult));
                if (currentCheckpoint is not null)
                {
                    var (ok, _) = await PersistRollbackCheckpointAsync(
                        session, allAttempts, rbSourceIndexes,
                        RecoverySessionLifecycle.PositionUncertain, true, true, failedStepIndex,
                        forwardFailureType, forwardFailureMessage,
                        "RollbackPreviousPositionMismatch",
                        $"回滚步骤 #{si} PreviousPosition ({rbResult.PreviousPosition.X},{rbResult.PreviousPosition.Y}) ≠ 期望 ({expectedPrevious.X},{expectedPrevious.Y})。",
                        currentCheckpoint);
                    if (!ok)
                    {
                        return PositionUncertainS(session, failedStepIndex, allAttempts,
                            executionPlan, safetySnapshotId,
                            forwardFailureType, forwardFailureMessage,
                            "TerminalPersistFailed",
                            "RollbackPreviousPositionMismatch 后终态持久化失败。");
                    }
                }
                return PositionUncertainS(session, failedStepIndex, allAttempts,
                    executionPlan, safetySnapshotId,
                    forwardFailureType, forwardFailureMessage,
                    "RollbackPreviousPositionMismatch",
                    $"回滚步骤 #{si} PreviousPosition ({rbResult.PreviousPosition.X},{rbResult.PreviousPosition.Y}) ≠ 期望 ({expectedPrevious.X},{expectedPrevious.Y})。");
            }

            if (!rbResult.IsExact)
            {
                allAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromInexactRollback(
                    attemptIndex, si, step, rollbackTarget, expectedPrevious, rbResult));
                if (currentCheckpoint is not null)
                {
                    var (ok, _) = await PersistRollbackCheckpointAsync(
                        session, allAttempts, rbSourceIndexes,
                        RecoverySessionLifecycle.FailedRollbackIncomplete, true, false, failedStepIndex,
                        forwardFailureType, forwardFailureMessage,
                        "RollbackInexact",
                        $"回滚步骤 #{si} 非精确：Actual=({rbResult.ActualPosition.X},{rbResult.ActualPosition.Y}), Target=({rollbackTarget.X},{rollbackTarget.Y})。",
                        currentCheckpoint);
                    if (!ok)
                    {
                        return PositionUncertainS(session, failedStepIndex, allAttempts,
                            executionPlan, safetySnapshotId,
                            forwardFailureType, forwardFailureMessage,
                            "TerminalPersistFailed",
                            "RollbackInexact 后终态持久化失败。");
                    }
                }
                return FailedRollbackIncompleteS(session, failedStepIndex, allAttempts,
                    executionPlan, safetySnapshotId,
                    forwardFailureType, forwardFailureMessage,
                    "RollbackInexact",
                    $"回滚步骤 #{si} 非精确：Actual=({rbResult.ActualPosition.X},{rbResult.ActualPosition.Y}), Target=({rollbackTarget.X},{rollbackTarget.Y})。");
            }

            allAttempts.Add(TargetDisplayLayoutExecutionAttemptResult.FromExactRollback(
                attemptIndex, si, step, rollbackTarget, expectedPrevious, rbResult));
            attemptIndex++;
            rbSourceIndexes.Add(si);

            if (currentCheckpoint is not null)
            {
                var (ok, newCp) = await PersistRollbackCheckpointAsync(
                    session, allAttempts, rbSourceIndexes,
                    RecoverySessionLifecycle.Executing, false, false, failedStepIndex,
                    forwardFailureType, forwardFailureMessage,
                    null, null,
                    currentCheckpoint);
                if (!ok)
                {
                    return PositionUncertainS(session, failedStepIndex, allAttempts,
                        executionPlan, safetySnapshotId,
                        forwardFailureType, forwardFailureMessage,
                        "RollbackCheckpointPersistFailed",
                        "Rollback checkpoint 持久化失败。");
                }
                currentCheckpoint = newCp!;
            }
        }

        var allForward = allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward).ToList();
        var allRollback = allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Rollback).ToList();
        var rollbackSourceIndexes = allRollback
            .Where(a => a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
            .Select(a => a.SourceStepIndex).ToList();

        TargetDisplayLayoutExecutionVerificationResult rbVerification;
        try
        {
            rbVerification = await _verifyRollback(executionPlan, session, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var issue = $"回滚后验证抛出异常：{ex.GetType().Name}: {ex.Message}";
            var uncertainVerification = TargetDisplayLayoutExecutionVerificationResult.RollbackUncertain(
                0, new[] { issue }, DateTimeOffset.UtcNow);

            if (currentCheckpoint is not null)
            {
                var (ok, _) = await TryUpdateAndGetVerifiedAsync(
                    session, allForward, allRollback,
                    allForward.Where(a => a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
                        .Select(a => a.SourceStepIndex).Distinct().OrderBy(x => x).ToList(),
                    rollbackSourceIndexes.Distinct().OrderBy(x => x).ToList(),
                    RecoverySessionLifecycle.PositionUncertain, true, true, failedStepIndex,
                    forwardFailureType, forwardFailureMessage,
                    "RollbackVerificationException", issue,
                    uncertainVerification,
                    currentCheckpoint);
                if (!ok)
                {
                    return PositionUncertainS(session, failedStepIndex, allAttempts,
                        executionPlan, safetySnapshotId,
                        forwardFailureType, forwardFailureMessage,
                        "TerminalPersistFailed",
                        "RollbackVerificationException 后终态持久化失败。");
                }
            }

            var rbeSession = BuildSession(session, allForward, allRollback,
                failedStepIndex, true, true, "PositionUncertain");
            return new TargetDisplayLayoutExecutionRunResult(
                TargetDisplayLayoutExecutionRunStatus.PositionUncertain,
                false, false, rbeSession, safetySnapshotId,
                allForward, allRollback,
                failedStepIndex, true, true,
                forwardFailureType, forwardFailureMessage,
                "RollbackVerificationException", issue,
                uncertainVerification);
        }

        var (rbLifecycle, rbManual, rbUncertain, rbRecoveryType, rbRecoveryMsg) = rbVerification.Status switch
        {
            ExecutionVerificationStatus.Passed =>
                (RecoverySessionLifecycle.FailedAndRolledBack, false, false, recoveryFailureType, recoveryFailureMessage),
            ExecutionVerificationStatus.Failed =>
                (RecoverySessionLifecycle.FailedRollbackIncomplete, true, false, "RollbackVerificationFailed",
                 $"回滚后独立验证失败：" + string.Join("; ", rbVerification.Issues)),
            ExecutionVerificationStatus.Uncertain =>
                (RecoverySessionLifecycle.PositionUncertain, true, true, "RollbackVerificationUncertain",
                 $"回滚后独立验证不确定：" + string.Join("; ", rbVerification.Issues)),
            _ => (RecoverySessionLifecycle.PositionUncertain, true, true, "RollbackVerificationUnknown", "未知验证状态。")
        };

        if (currentCheckpoint is not null)
        {
            var (ok, _) = await TryUpdateAndGetVerifiedAsync(
                session, allForward, allRollback,
                allForward.Where(a => a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
                    .Select(a => a.SourceStepIndex).Distinct().OrderBy(x => x).ToList(),
                rollbackSourceIndexes.Distinct().OrderBy(x => x).ToList(),
                rbLifecycle, rbManual, rbUncertain, failedStepIndex,
                forwardFailureType, forwardFailureMessage,
                rbRecoveryType, rbRecoveryMsg,
                rbVerification,
                currentCheckpoint);
            if (!ok)
            {
                return PositionUncertainS(session, failedStepIndex, allAttempts,
                    executionPlan, safetySnapshotId,
                    forwardFailureType, forwardFailureMessage,
                    "TerminalPersistFailed",
                    "Rollback 终态持久化失败。");
            }
        }

        var resultSession = BuildSession(session, allForward, allRollback,
            failedStepIndex, rbManual, rbUncertain,
            rbLifecycle == RecoverySessionLifecycle.FailedAndRolledBack ? "FailedAndRolledBack"
            : rbLifecycle == RecoverySessionLifecycle.FailedRollbackIncomplete ? "FailedRollbackIncomplete"
            : "PositionUncertain");
        return new TargetDisplayLayoutExecutionRunResult(
            rbLifecycle == RecoverySessionLifecycle.FailedAndRolledBack
                ? TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack
                : rbLifecycle == RecoverySessionLifecycle.FailedRollbackIncomplete
                    ? TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete
                    : TargetDisplayLayoutExecutionRunStatus.PositionUncertain,
            false, false, resultSession, safetySnapshotId,
            allForward, allRollback,
            failedStepIndex, rbManual, rbUncertain,
            forwardFailureType, forwardFailureMessage,
            rbRecoveryType, rbRecoveryMsg,
            rbVerification);
    }

    private async Task<TargetDisplayLayoutExecutionRunResult> RollbackKnownS_Legacy(
        TargetDisplayLayoutRecoverySession session,
        int failedStepIndex,
        List<TargetDisplayLayoutExecutionAttemptResult> allAttempts,
        TargetDisplayLayoutExecutionPlan executionPlan,
        Guid safetySnapshotId,
        int attemptIndex,
        string forwardFailureType,
        string forwardFailureMessage,
        string? recoveryFailureType,
        string? recoveryFailureMessage)
    {
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

        var allForward = allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward).ToList();
        var allRollback = allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Rollback).ToList();
        var rollbackSourceIndexes = allRollback
            .Where(a => a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
            .Select(a => a.SourceStepIndex).ToList();

        TargetDisplayLayoutExecutionVerificationResult rbVerification;
        try
        {
            rbVerification = await _verifyRollback(executionPlan, session, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var issue = $"回滚后验证抛出异常：{ex.GetType().Name}: {ex.Message}";
            var uncertainVerification = TargetDisplayLayoutExecutionVerificationResult.RollbackUncertain(
                0, new[] { issue }, DateTimeOffset.UtcNow);
            var rbeSession = BuildSession(session, allForward, allRollback,
                failedStepIndex, true, true, "PositionUncertain");
            return new TargetDisplayLayoutExecutionRunResult(
                TargetDisplayLayoutExecutionRunStatus.PositionUncertain,
                false, false, rbeSession, safetySnapshotId,
                allForward, allRollback,
                failedStepIndex, true, true,
                forwardFailureType, forwardFailureMessage,
                "RollbackVerificationException", issue,
                uncertainVerification);
        }

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
                    failedStepIndex, false, false, "FailedAndRolledBack",
                    origin: passedSession.Origin);

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

            default:
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

    internal static (string ErrorType, string ErrorMessage)? ValidateExecutionPlanStructure(
        TargetDisplayLayoutExecutionPlan plan)
    {
        if (!plan.IsExecutable)
            return ("PlanNotExecutable", "计划不可执行。");
        if (plan.IsNoOp)
            return null;

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
            failedStepIndex, requiresManual, isPositionUncertain, status,
            origin: original.Origin);

    private async Task<TargetDisplayLayoutExecutionVerificationResult> VerifyForwardAsync(
        TargetDisplayLayoutExecutionPlan executionPlan,
        TargetDisplayLayoutRecoverySession session,
        CancellationToken cancellationToken)
    {
        var verifiedAt = DateTimeOffset.UtcNow;
        var targetDeviceName = executionPlan.TargetDeviceName;

        DesktopIconReadResult currentRead;
        IReadOnlyList<DisplayDescriptor> currentDisplays;
        try
        {
            currentDisplays = _topologyReader.GetDisplays();
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
            return TargetDisplayLayoutExecutionVerificationResult.ForwardUncertain(
                0, new[] { $"Forward 验证读取异常：{ex.GetType().Name}: {ex.Message}" }, verifiedAt);
        }

        var issues = new List<string>();

        if (currentRead.FailedItemCount != 0)
        {
            issues.Add($"FailedItemCount = {currentRead.FailedItemCount}，读取不完整。");
            return TargetDisplayLayoutExecutionVerificationResult.ForwardUncertain(
                currentRead.FailedItemCount, issues, verifiedAt);
        }

        var sidSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var icon in currentRead.Icons)
        {
            if (!sidSet.Add(icon.StableId))
                issues.Add($"StableId \"{icon.StableId}\" 重复。");
        }
        if (issues.Count > 0)
            return TargetDisplayLayoutExecutionVerificationResult.ForwardUncertain(
                0, issues, verifiedAt);

        var currentBySid = new Dictionary<string, DesktopIconDescriptor>(StringComparer.Ordinal);
        foreach (var icon in currentRead.Icons)
            currentBySid[icon.StableId] = icon;

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
                if (item.State == TargetDisplayLayoutRestoreItemState.MoveRequired
                    || item.State == TargetDisplayLayoutRestoreItemState.AlreadyAtTarget)
                {
                    issues.Add($"\"{item.StableId}\" Forward 验证显示器 " +
                        $"\"{currentIcon.MonitorDeviceName}\" ≠ 目标 \"{targetDeviceName}\"。");
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

        var rollbackTargets =
            new Dictionary<string, TargetDisplayLayoutRestorePlanItem>(StringComparer.Ordinal);
        foreach (var step in executionPlan.Steps)
        {
            var matches = session.SourceRestorePlan.Items
                .Where(item => string.Equals(
                    item.StableId, step.StableId, StringComparison.Ordinal))
                .ToList();

            if (matches.Count != 1 || matches[0].CurrentPosition is null)
            {
                return TargetDisplayLayoutExecutionVerificationResult.RollbackUncertain(
                    0,
                    new[]
                    {
                        $"StableId \"{step.StableId}\" 无法唯一映射到含原始位置的 SourceRestorePlan 项。"
                    },
                    verifiedAt);
            }

            rollbackTargets.TryAdd(step.StableId, matches[0]);
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

        var notRestored = 0;
        foreach (var (sid, sourceItem) in rollbackTargets)
        {
            if (!currentBySid.TryGetValue(sid, out var currentIcon))
            {
                issues.Add($"StableId \"{sid}\" 在 Rollback 验证读取中缺失。");
                return TargetDisplayLayoutExecutionVerificationResult.RollbackUncertain(
                    0, issues, verifiedAt);
            }

            var expectedPos = sourceItem.CurrentPosition!.Value;
            var itemNotRestored = false;

            if (currentIcon.PixelPosition != expectedPos)
            {
                issues.Add($"\"{sid}\" Rollback 验证位置 " +
                    $"({currentIcon.PixelPosition.X},{currentIcon.PixelPosition.Y}) ≠ " +
                    $"原始 CurrentPosition ({expectedPos.X},{expectedPos.Y})。");
                itemNotRestored = true;
            }

            if (!string.Equals(
                    currentIcon.MonitorDeviceName,
                    sourceItem.CurrentMonitorDeviceName,
                    StringComparison.Ordinal))
            {
                issues.Add($"\"{sid}\" Rollback 验证显示器 " +
                    $"\"{currentIcon.MonitorDeviceName ?? "<null>"}\" ≠ " +
                    $"原始 \"{sourceItem.CurrentMonitorDeviceName ?? "<null>"}\"。");
                itemNotRestored = true;
            }

            if (itemNotRestored)
                notRestored++;
        }

        if (notRestored > 0)
            return TargetDisplayLayoutExecutionVerificationResult.RollbackFailed(
                notRestored, issues, verifiedAt);

        if (issues.Count > 0)
            return TargetDisplayLayoutExecutionVerificationResult.RollbackUncertain(
                0, issues, verifiedAt);

        return TargetDisplayLayoutExecutionVerificationResult.RollbackPassed(verifiedAt);
    }

    private async Task<(bool Ok, RecoverySessionRecord? NewCp)> PersistRollbackCheckpointAsync(
        TargetDisplayLayoutRecoverySession session,
        List<TargetDisplayLayoutExecutionAttemptResult> allAttempts,
        List<int> rbSourceIndexes,
        RecoverySessionLifecycle lifecycle,
        bool requiresManual, bool isPositionUncertain,
        int? failedStepIndex,
        string? fwdFailType, string? fwdFailMsg,
        string? recFailType, string? recFailMsg,
        RecoverySessionRecord currentCheckpoint)
    {
        if (_recoveryStore is null) return (true, null);
        return await TryUpdateAndGetVerifiedAsync(
            session,
            allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward).ToList(),
            allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Rollback).ToList(),
            allAttempts.Where(a => a.Phase == TargetDisplayLayoutExecutionAttemptPhase.Forward
                && a.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
                .Select(a => a.SourceStepIndex).Distinct().OrderBy(x => x).ToList(),
            rbSourceIndexes.Distinct().OrderBy(x => x).ToList(),
            lifecycle, requiresManual, isPositionUncertain, failedStepIndex,
            fwdFailType, fwdFailMsg, recFailType, recFailMsg, null,
            currentCheckpoint);
    }

    private async Task<(bool Ok, RecoverySessionRecord? Record)> TrySaveInitialCheckpointAsync(
        TargetDisplayLayoutRecoverySession session,
        TargetDisplayLayoutExecutionPlan executionPlan,
        Guid safetySnapshotId)
    {
        if (_recoveryStore is null) return (true, null);
        try
        {
            var record = new RecoverySessionRecord
            {
                SessionId = session.SessionId,
                SafetySnapshotId = safetySnapshotId,
                TargetDeviceName = session.TargetDeviceName,
                StartedAtUtc = session.StartedAtUtc,
                Lifecycle = RecoverySessionLifecycle.Executing,
                // 来路必须一路带到落库那一刻：撤销写下的每一笔标成 CheckpointRestore，
                // 否则第二行默认的 Organize 会让「撤销」自己变成下一次撤销的候选。
                Origin = session.Origin,
                LastFailedStepIndex = null,
                SourceRestorePlan = session.SourceRestorePlan,
                ExecutionPlan = executionPlan,
                ForwardAttempts = Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
                RollbackAttempts = Array.Empty<TargetDisplayLayoutExecutionAttemptResult>(),
                CompletedForwardStepIndexes = Array.Empty<int>(),
                CompletedRollbackSourceStepIndexes = Array.Empty<int>(),
                Revision = 1,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            record.Validate();
            await _recoveryStore.SaveAsync(record);
            var readback = await _recoveryStore.GetAsync(record.SessionId);
            if (readback is null) return (false, null);
            if (!RecordsAreEquivalent(record, readback)) return (false, null);
            if (readback.Lifecycle != RecoverySessionLifecycle.Executing) return (false, null);
            if (readback.Revision != 1) return (false, null);
            if (readback.ForwardAttempts.Count != 0) return (false, null);
            if (readback.RollbackAttempts.Count != 0) return (false, null);
            if (readback.CompletedForwardStepIndexes.Count != 0) return (false, null);
            if (readback.CompletedRollbackSourceStepIndexes.Count != 0) return (false, null);
            return (true, readback);
        }
        catch { return (false, null); }
    }

    private async Task<(bool Ok, RecoverySessionRecord? Record)> TryUpdateAndGetVerifiedAsync(
        TargetDisplayLayoutRecoverySession session,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> forwardAttempts,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> rollbackAttempts,
        IReadOnlyList<int> completedFwd,
        IReadOnlyList<int> completedRb,
        RecoverySessionLifecycle lifecycle,
        bool requiresManualRecovery,
        bool isPositionUncertain,
        int? lastFailedStepIndex,
        string? forwardFailureType,
        string? forwardFailureMessage,
        string? recoveryFailureType,
        string? recoveryFailureMessage,
        TargetDisplayLayoutExecutionVerificationResult? verificationResult,
        RecoverySessionRecord currentCheckpoint)
    {
        if (_recoveryStore is null) return (true, null);
        try
        {
            var record = new RecoverySessionRecord
            {
                SessionId = session.SessionId,
                SafetySnapshotId = session.SafetySnapshotId,
                TargetDeviceName = session.TargetDeviceName,
                StartedAtUtc = session.StartedAtUtc,
                SourceRestorePlan = session.SourceRestorePlan,
                ExecutionPlan = session.ExecutionPlan,
                Lifecycle = lifecycle,
                Origin = session.Origin,
                LastFailedStepIndex = lastFailedStepIndex,
                ForwardAttempts = forwardAttempts,
                RollbackAttempts = rollbackAttempts,
                CompletedForwardStepIndexes = completedFwd,
                CompletedRollbackSourceStepIndexes = completedRb,
                ForwardFailureType = forwardFailureType,
                ForwardFailureMessage = forwardFailureMessage,
                RecoveryFailureType = recoveryFailureType,
                RecoveryFailureMessage = recoveryFailureMessage,
                VerificationResult = verificationResult,
                Revision = currentCheckpoint.Revision,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            record.Validate();
            var updated = await _recoveryStore.UpdateAsync(record);
            var fresh = await _recoveryStore.GetAsync(record.SessionId);
            if (fresh is null || !RecordsAreEquivalent(updated, fresh))
                return (false, null);
            return (true, fresh);
        }
        catch { return (false, null); }
    }

    internal static bool RecordsAreEquivalent(RecoverySessionRecord a, RecoverySessionRecord b)
    {
        if (a is null || b is null) return false;
        if (ReferenceEquals(a, b)) return true;

        if (a.SessionId != b.SessionId) return false;
        if (a.SafetySnapshotId != b.SafetySnapshotId) return false;
        if (!string.Equals(a.TargetDeviceName, b.TargetDeviceName, StringComparison.Ordinal)) return false;
        if (a.StartedAtUtc != b.StartedAtUtc) return false;
        if (a.UpdatedAtUtc != b.UpdatedAtUtc) return false;

        if (a.Lifecycle != b.Lifecycle) return false;
        if (a.LastFailedStepIndex != b.LastFailedStepIndex) return false;
        if (a.Revision != b.Revision) return false;

        if (!string.Equals(a.ForwardFailureType, b.ForwardFailureType, StringComparison.Ordinal)) return false;
        if (!string.Equals(a.ForwardFailureMessage, b.ForwardFailureMessage, StringComparison.Ordinal)) return false;
        if (!string.Equals(a.RecoveryFailureType, b.RecoveryFailureType, StringComparison.Ordinal)) return false;
        if (!string.Equals(a.RecoveryFailureMessage, b.RecoveryFailureMessage, StringComparison.Ordinal)) return false;

        if (!a.CompletedForwardStepIndexes.SequenceEqual(b.CompletedForwardStepIndexes)) return false;
        if (!a.CompletedRollbackSourceStepIndexes.SequenceEqual(b.CompletedRollbackSourceStepIndexes)) return false;

        if (!AttemptListsMatch(a.ForwardAttempts, b.ForwardAttempts)) return false;
        if (!AttemptListsMatch(a.RollbackAttempts, b.RollbackAttempts)) return false;

        if (a.VerificationResult is null != b.VerificationResult is null) return false;
        if (a.VerificationResult is not null && b.VerificationResult is not null)
        {
            if (a.VerificationResult.Phase != b.VerificationResult.Phase) return false;
            if (a.VerificationResult.Status != b.VerificationResult.Status) return false;
            if (a.VerificationResult.FailedItemCount != b.VerificationResult.FailedItemCount) return false;
            if (a.VerificationResult.VerifiedAtUtc != b.VerificationResult.VerifiedAtUtc) return false;
            if (!a.VerificationResult.Issues.SequenceEqual(b.VerificationResult.Issues, StringComparer.Ordinal)) return false;
        }

        if (!RestorePlansMatch(a.SourceRestorePlan, b.SourceRestorePlan)) return false;
        if (!ExecPlansMatch(a.ExecutionPlan, b.ExecutionPlan)) return false;

        return true;
    }

    private static bool AttemptListsMatch(
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> a,
        IReadOnlyList<TargetDisplayLayoutExecutionAttemptResult> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i]; var y = b[i];
            if (x.AttemptIndex != y.AttemptIndex) return false;
            if (x.SourceStepIndex != y.SourceStepIndex) return false;
            if (x.Phase != y.Phase) return false;
            if (!string.Equals(x.ExpectedStableId, y.ExpectedStableId, StringComparison.Ordinal)) return false;
            if (x.ExpectedPreviousPosition != y.ExpectedPreviousPosition) return false;
            if (x.RequestedPosition != y.RequestedPosition) return false;
            if (!string.Equals(x.ReportedStableId, y.ReportedStableId, StringComparison.Ordinal)) return false;
            if (x.ReportedPreviousPosition != y.ReportedPreviousPosition) return false;
            if (x.ActualPosition != y.ActualPosition) return false;
            if (x.HasTrustedActualPosition != y.HasTrustedActualPosition) return false;
            if (x.IsExact != y.IsExact) return false;
            if (x.Outcome != y.Outcome) return false;
            if (x.ConnectionMethod != y.ConnectionMethod) return false;
            if (!string.Equals(x.ExceptionType, y.ExceptionType, StringComparison.Ordinal)) return false;
            if (!string.Equals(x.ErrorMessage, y.ErrorMessage, StringComparison.Ordinal)) return false;
            if (!string.Equals(x.PrimaryPathExceptionType, y.PrimaryPathExceptionType, StringComparison.Ordinal)) return false;
            if (!string.Equals(x.PrimaryPathExceptionMessage, y.PrimaryPathExceptionMessage, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool RestorePlansMatch(TargetDisplayLayoutRestorePlan a, TargetDisplayLayoutRestorePlan b)
    {
        if (!string.Equals(a.TargetDeviceName, b.TargetDeviceName, StringComparison.Ordinal)) return false;
        if (a.SnapshotTargetIconCount != b.SnapshotTargetIconCount) return false;
        if (a.AlreadyAtTargetCount != b.AlreadyAtTargetCount) return false;
        if (a.MoveRequiredCount != b.MoveRequiredCount) return false;
        if (a.MissingCount != b.MissingCount) return false;
        if (a.BlockedCount != b.BlockedCount) return false;
        if (a.HasBlockingErrors != b.HasBlockingErrors) return false;
        if (a.RequiresTemporaryStaging != b.RequiresTemporaryStaging) return false;
        if (a.IsNoOp != b.IsNoOp) return false;
        if (a.Items.Count != b.Items.Count) return false;
        for (int i = 0; i < a.Items.Count; i++)
        {
            var ai = a.Items[i]; var bi = b.Items[i];
            if (!string.Equals(ai.StableId, bi.StableId, StringComparison.Ordinal)) return false;
            if (!string.Equals(ai.DisplayName, bi.DisplayName, StringComparison.Ordinal)) return false;
            if (ai.SnapshotTargetPosition != bi.SnapshotTargetPosition) return false;
            if (ai.CurrentPosition != bi.CurrentPosition) return false;
            if (!string.Equals(ai.CurrentMonitorDeviceName, bi.CurrentMonitorDeviceName, StringComparison.Ordinal)) return false;
            if (ai.State != bi.State) return false;
            if (ai.IsFileSystemItem != bi.IsFileSystemItem) return false;
            if (ai.IsSystemItem != bi.IsSystemItem) return false;
            if (!string.Equals(ai.BlockReason, bi.BlockReason, StringComparison.Ordinal)) return false;
        }
        if (a.Dependencies.Count != b.Dependencies.Count) return false;
        for (int i = 0; i < a.Dependencies.Count; i++)
        {
            if (!string.Equals(a.Dependencies[i].DependsOn, b.Dependencies[i].DependsOn, StringComparison.Ordinal)) return false;
            if (!string.Equals(a.Dependencies[i].ActionItem, b.Dependencies[i].ActionItem, StringComparison.Ordinal)) return false;
        }
        if (a.Cycles.Count != b.Cycles.Count) return false;
        for (int i = 0; i < a.Cycles.Count; i++)
        {
            if (!a.Cycles[i].StableIds.SequenceEqual(b.Cycles[i].StableIds, StringComparer.Ordinal)) return false;
        }
        if (a.Issues.Count != b.Issues.Count) return false;
        for (int i = 0; i < a.Issues.Count; i++)
        {
            if (a.Issues[i].Code != b.Issues[i].Code) return false;
            if (!string.Equals(a.Issues[i].Message, b.Issues[i].Message, StringComparison.Ordinal)) return false;
            if (!string.Equals(a.Issues[i].RelatedStableId, b.Issues[i].RelatedStableId, StringComparison.Ordinal)) return false;
        }
        if ((a.MoveExecutionOrder is null) != (b.MoveExecutionOrder is null)) return false;
        if (a.MoveExecutionOrder is not null && b.MoveExecutionOrder is not null)
        {
            if (!a.MoveExecutionOrder.SequenceEqual(b.MoveExecutionOrder, StringComparer.Ordinal)) return false;
        }
        return true;
    }

    private static bool ExecPlansMatch(TargetDisplayLayoutExecutionPlan a, TargetDisplayLayoutExecutionPlan b)
    {
        if (!string.Equals(a.TargetDeviceName, b.TargetDeviceName, StringComparison.Ordinal)) return false;
        if (a.IsExecutable != b.IsExecutable) return false;
        if (a.IsNoOp != b.IsNoOp) return false;
        if (a.UsesTemporaryStaging != b.UsesTemporaryStaging) return false;
        if (a.DirectMoveStepCount != b.DirectMoveStepCount) return false;
        if (a.StagingMoveStepCount != b.StagingMoveStepCount) return false;
        if (a.TargetMoveStepCount != b.TargetMoveStepCount) return false;
        if (a.Steps.Count != b.Steps.Count) return false;
        for (int i = 0; i < a.Steps.Count; i++)
        {
            var sa = a.Steps[i]; var sb = b.Steps[i];
            if (sa.StepIndex != sb.StepIndex) return false;
            if (!string.Equals(sa.StableId, sb.StableId, StringComparison.Ordinal)) return false;
            if (!string.Equals(sa.DisplayName, sb.DisplayName, StringComparison.Ordinal)) return false;
            if (sa.FromPosition != sb.FromPosition) return false;
            if (sa.ToPosition != sb.ToPosition) return false;
            if (sa.Kind != sb.Kind) return false;
            if (!string.Equals(sa.ExpectedMonitorDeviceName, sb.ExpectedMonitorDeviceName, StringComparison.Ordinal)) return false;
            if (sa.StagingSequence != sb.StagingSequence) return false;
        }
        if (a.StagingAssignments.Count != b.StagingAssignments.Count) return false;
        for (int i = 0; i < a.StagingAssignments.Count; i++)
        {
            var sa = a.StagingAssignments[i]; var sb = b.StagingAssignments[i];
            if (!string.Equals(sa.StableId, sb.StableId, StringComparison.Ordinal)) return false;
            if (sa.StagingPosition != sb.StagingPosition) return false;
            if (sa.EnterStepIndex != sb.EnterStepIndex) return false;
            if (sa.ExitStepIndex != sb.ExitStepIndex) return false;
        }
        if (a.Issues.Count != b.Issues.Count) return false;
        for (int i = 0; i < a.Issues.Count; i++)
        {
            if (a.Issues[i].Code != b.Issues[i].Code) return false;
            if (!string.Equals(a.Issues[i].Message, b.Issues[i].Message, StringComparison.Ordinal)) return false;
            if (!string.Equals(a.Issues[i].RelatedStableId, b.Issues[i].RelatedStableId, StringComparison.Ordinal)) return false;
        }
        return true;
    }
}
