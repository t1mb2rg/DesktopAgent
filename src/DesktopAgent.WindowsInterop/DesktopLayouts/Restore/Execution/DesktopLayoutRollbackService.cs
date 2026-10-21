using DesktopAgent.Application.DesktopObservations;
using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;
using DesktopAgent.WindowsInterop.DesktopIcons;
using DesktopAgent.WindowsInterop.DesktopObservations;
using DesktopAgent.WindowsInterop.Displays;

namespace DesktopAgent.WindowsInterop.DesktopLayouts.Restore.Execution;

public enum DesktopLayoutRollbackOutcome
{
    Completed = 0,
    CompletedNoOp = 1,
    PreflightBlocked = 2,
    UnresolvedRecoveryBlocked = 3,
    PlanRejected = 4,
    ScopeRejected = 5,
    ExecutionFailed = 6,
}

/// <summary>
/// 「撤销上次整理」的受控结果。真实写入是否发生，以 <see cref="ExecutionResult"/> 为准。
/// </summary>
public sealed record DesktopLayoutRollbackResult
{
    public DesktopLayoutRollbackOutcome Outcome { get; }

    /// <summary>不能撤销时的原因（能撤销或已执行时为 null）。</summary>
    public DesktopLayoutRollbackBlockReason? BlockReason { get; }
    public TargetDisplayLayoutExecutionRunResult? ExecutionResult { get; }

    /// <summary>这次撤销实际跑过的每一屏还原执行（跨屏整理过后会有多笔，按执行顺序）。</summary>
    public IReadOnlyList<TargetDisplayLayoutExecutionRunResult> Executions { get; }
    public DesktopLayoutRollbackPreflightResult? Preflight { get; }
    public string Headline { get; }
    public string Detail { get; }
    public IReadOnlyList<string> Issues { get; }

    public bool IsSuccess => Outcome is
        DesktopLayoutRollbackOutcome.Completed or
        DesktopLayoutRollbackOutcome.CompletedNoOp;

    public bool RequiresManualRecovery => ExecutionResult?.RequiresManualRecovery == true;
    public bool IsPositionUncertain => ExecutionResult?.IsPositionUncertain == true;

    public DesktopLayoutRollbackResult(
        DesktopLayoutRollbackOutcome outcome,
        string headline,
        string detail,
        TargetDisplayLayoutExecutionRunResult? executionResult = null,
        DesktopLayoutRollbackPreflightResult? preflight = null,
        IReadOnlyList<string>? issues = null,
        DesktopLayoutRollbackBlockReason? blockReason = null,
        IReadOnlyList<TargetDisplayLayoutExecutionRunResult>? executions = null)
    {
        Outcome = outcome;
        BlockReason = blockReason;
        Headline = headline;
        Detail = detail;
        ExecutionResult = executionResult;
        Preflight = preflight;
        Issues = Array.AsReadOnly((issues ?? Array.Empty<string>()).ToArray());
        Executions = Array.AsReadOnly((executions
            ?? (executionResult is null
                ? Array.Empty<TargetDisplayLayoutExecutionRunResult>()
                : new[] { executionResult })).ToArray());
    }

    internal static DesktopLayoutRollbackResult Rejected(
        DesktopLayoutRollbackOutcome outcome,
        string headline,
        string detail,
        DesktopLayoutRollbackBlockReason? blockReason = null,
        DesktopLayoutRollbackPreflightResult? preflight = null,
        params string[] issues)
        => new(outcome, headline, detail, null, preflight, issues, blockReason);

    internal static DesktopLayoutRollbackResult FromExecution(
        TargetDisplayLayoutExecutionRunResult executionResult,
        DesktopLayoutRollbackPreflightResult preflight)
    {
        ArgumentNullException.ThrowIfNull(executionResult);
        ArgumentNullException.ThrowIfNull(preflight);

        if (executionResult.IsSuccess)
        {
            return new DesktopLayoutRollbackResult(
                executionResult.IsNoOp
                    ? DesktopLayoutRollbackOutcome.CompletedNoOp
                    : DesktopLayoutRollbackOutcome.Completed,
                "已撤销上次整理",
                $"操作已经结束。{preflight.Detail}",
                executionResult,
                preflight);
        }

        var issues = new List<string>();
        if (!string.IsNullOrWhiteSpace(executionResult.ForwardFailureMessage))
            issues.Add(executionResult.ForwardFailureMessage);
        if (!string.IsNullOrWhiteSpace(executionResult.RecoveryFailureMessage))
            issues.Add(executionResult.RecoveryFailureMessage);

        return new DesktopLayoutRollbackResult(
            DesktopLayoutRollbackOutcome.ExecutionFailed,
            "撤销没有完整完成",
            "请查看恢复状态与下面的原因；在确认现场之前不要再手工挪动图标。",
            executionResult,
            preflight,
            issues);
    }

    /// <summary>
    /// 跨屏撤销：多屏各跑一笔后的收尾。全部成功才算撤销完成；
    /// 中途失败时把"已经还原了哪几屏、卡在哪一屏"如实写进说明里。
    /// </summary>
    internal static DesktopLayoutRollbackResult FromExecutions(
        IReadOnlyList<TargetDisplayLayoutExecutionRunResult> executions,
        DesktopLayoutRollbackPreflightResult preflight,
        IReadOnlyList<string> executedDeviceNames,
        string? partialNote = null,
        IReadOnlyList<string>? extraIssues = null)
    {
        ArgumentNullException.ThrowIfNull(executions);
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(executedDeviceNames);

        var last = executions.Count == 0 ? null : executions[^1];
        var failedIndex = -1;
        for (var i = 0; i < executions.Count; i++)
        {
            if (!executions[i].IsSuccess)
            {
                failedIndex = i;
                break;
            }
        }

        var stoppedWithoutFailure = failedIndex < 0
            && extraIssues is { Count: > 0 };

        if (failedIndex < 0 && !stoppedWithoutFailure)
        {
            var noOp = executions.All(execution => execution.IsNoOp);
            var scope = executedDeviceNames.Count > 1
                ? $"（涉及 {executedDeviceNames.Count} 块屏：{string.Join("、", executedDeviceNames)}）"
                : string.Empty;
            return new DesktopLayoutRollbackResult(
                noOp ? DesktopLayoutRollbackOutcome.CompletedNoOp : DesktopLayoutRollbackOutcome.Completed,
                "已撤销上次整理",
                $"操作已经结束。{preflight.Detail}{scope}",
                last,
                preflight,
                executions: executions);
        }

        var issues = new List<string>();
        if (partialNote is not null)
            issues.Add(partialNote);
        if (extraIssues is not null)
            issues.AddRange(extraIssues);
        if (failedIndex >= 0)
        {
            if (!string.IsNullOrWhiteSpace(executions[failedIndex].ForwardFailureMessage))
                issues.Add(executions[failedIndex].ForwardFailureMessage!);
            if (!string.IsNullOrWhiteSpace(executions[failedIndex].RecoveryFailureMessage))
                issues.Add(executions[failedIndex].RecoveryFailureMessage!);
        }

        return new DesktopLayoutRollbackResult(
            DesktopLayoutRollbackOutcome.ExecutionFailed,
            "撤销没有完整完成",
            "请查看恢复状态与下面的原因；在确认现场之前不要再手工挪动图标。",
            failedIndex >= 0 ? executions[failedIndex] : last,
            preflight,
            issues,
            executions: executions);
    }
}

/// <summary>
/// 「撤销上次整理」的产品边界。
///
/// 语义：把桌面放回<strong>最近一次成功整理之前</strong>的安全检查点，而不是把每一步反向挪一次。
/// 因此这里做三件事，缺一不可：
/// ① 只读前置判定（<see cref="DesktopLayoutRollbackPreflight"/>）回答"有没有可信的原样、桌面是否还是我们整理完的样子"；
/// ② 按快照位置生成还原计划（<c>TargetDisplayLayoutRestorePlanner</c>），目标位置全部来自那次写盘前的快照；
/// ③ 走与「应用到桌面」<strong>完全相同</strong>的受控执行入口：同一套 unresolved 现场 interlock、
///    同一个 ScopeGate、同一个 Coordinator（它会再读一次桌面、在第一笔写入前保存并回读安全检查点）。
///
/// 一个字都不凭推断：拿不到快照、桌面被改过、有未收敛现场，一律拒绝并说明原因。
/// </summary>
public sealed class DesktopLayoutRollbackService
{
    private readonly IDesktopObservationSource _observationSource;
    private readonly ICommittedRecoverySessionQuery _committedSessions;
    private readonly IDesktopLayoutSnapshotStore _snapshotStore;
    private readonly IRecoverySessionStore _recoveryStore;
    private readonly IDesktopWorkspaceExecutionJournalStore? _workspaceExecutionJournalStore;
    private readonly IDesktopWorkspaceDynamicExecutionJournalStore? _dynamicWorkspaceExecutionJournalStore;
    private readonly ICommittedWorkspaceExecutionJournalQuery? _committedWorkspaceWrites;
    private readonly Func<TargetDisplayLayoutRestorePlan, bool, CancellationToken,
        Task<TargetDisplayLayoutExecutionRunResult>> _executeAuthorizedAsync;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _rollbackLock = new(1, 1);

    /// <summary>当前产品构造器：内部装配 ObservationSource 与执行协调器，与直接应用同一套。</summary>
    public DesktopLayoutRollbackService(
        ICommittedRecoverySessionQuery committedSessions,
        IDesktopLayoutSnapshotStore snapshotStore,
        IRecoverySessionStore recoveryStore,
        IDesktopWorkspaceExecutionJournalStore? workspaceExecutionJournalStore = null,
        IDesktopWorkspaceDynamicExecutionJournalStore? dynamicWorkspaceExecutionJournalStore = null,
        TimeProvider? timeProvider = null,
        ICommittedWorkspaceExecutionJournalQuery? committedWorkspaceWrites = null)
    {
        _committedSessions = committedSessions
            ?? throw new ArgumentNullException(nameof(committedSessions));
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        _recoveryStore = recoveryStore ?? throw new ArgumentNullException(nameof(recoveryStore));
        _workspaceExecutionJournalStore = workspaceExecutionJournalStore;
        _dynamicWorkspaceExecutionJournalStore = dynamicWorkspaceExecutionJournalStore;
        _timeProvider = timeProvider ?? TimeProvider.System;

        // 产品里装配的是同一个 Sqlite 动态 journal 存储：它同时实现已收尾查询，
        // 于是撤销入口天然看得到「工作区设计应用」这条路。显式参数只用于测试。
        _committedWorkspaceWrites = committedWorkspaceWrites
            ?? dynamicWorkspaceExecutionJournalStore as ICommittedWorkspaceExecutionJournalQuery;

        var topologyReader = new NativeDisplayTopologyReader();
        var iconReader = new DesktopShellIconReader();
        var capabilitiesReader = new DesktopShellIconPositioningCapabilitiesReader();
        var positionWriter = new DesktopShellIconPositionWriter();
        var staExecutor = new StaThreadExecutor();
        var snapshotFactory = new DesktopLayoutSnapshotFactory(_timeProvider);

        _observationSource = new WindowsDesktopObservationSource(
            topologyReader,
            iconReader,
            capabilitiesReader,
            staExecutor,
            _timeProvider);

        var coordinator = new TargetDisplayLayoutExecutionCoordinator(
            topologyReader,
            iconReader,
            positionWriter,
            staExecutor,
            new DefaultSafetySnapshotFactory(snapshotFactory),
            snapshotStore,
            recoveryStore);

        _executeAuthorizedAsync = (restorePlan, usesTemporaryStaging, ct) =>
            coordinator.ExecuteWithScopeGateAsync(
                restorePlan,
                SelectAuthorizedScopeGate(usesTemporaryStaging),
                ct,
                // 撤销写下的每一笔都标成「检查点还原」：它不能再变成下一次撤销的候选，
                // 否则用户点第二次「撤销」就会把桌面重新整理一遍。
                RecoverySessionOrigin.CheckpointRestore);
    }

    /// <summary>测试/组合入口：替换观察源与最终受控执行委托，其余判定与装配不变。</summary>
    internal DesktopLayoutRollbackService(
        IDesktopObservationSource observationSource,
        ICommittedRecoverySessionQuery committedSessions,
        IDesktopLayoutSnapshotStore snapshotStore,
        IRecoverySessionStore recoveryStore,
        Func<TargetDisplayLayoutRestorePlan, bool, CancellationToken,
            Task<TargetDisplayLayoutExecutionRunResult>> executeAuthorizedAsync,
        IDesktopWorkspaceExecutionJournalStore? workspaceExecutionJournalStore = null,
        IDesktopWorkspaceDynamicExecutionJournalStore? dynamicWorkspaceExecutionJournalStore = null,
        TimeProvider? timeProvider = null,
        ICommittedWorkspaceExecutionJournalQuery? committedWorkspaceWrites = null)
    {
        _observationSource = observationSource ?? throw new ArgumentNullException(nameof(observationSource));
        _committedSessions = committedSessions ?? throw new ArgumentNullException(nameof(committedSessions));
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        _recoveryStore = recoveryStore ?? throw new ArgumentNullException(nameof(recoveryStore));
        _executeAuthorizedAsync = executeAuthorizedAsync
            ?? throw new ArgumentNullException(nameof(executeAuthorizedAsync));
        _workspaceExecutionJournalStore = workspaceExecutionJournalStore;
        _dynamicWorkspaceExecutionJournalStore = dynamicWorkspaceExecutionJournalStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _committedWorkspaceWrites = committedWorkspaceWrites
            ?? dynamicWorkspaceExecutionJournalStore as ICommittedWorkspaceExecutionJournalQuery;
    }

    /// <summary>
    /// 只读：现在能不能撤销、为什么、要挪回几个项目。UI 用它决定按钮状态与说明文案。
    /// </summary>
    public async Task<DesktopLayoutRollbackPreflightResult> PrepareAsync(
        CancellationToken cancellationToken = default)
    {
        await _snapshotStore.InitializeAsync(cancellationToken);
        await _recoveryStore.InitializeAsync(cancellationToken);

        var candidate = await ResolveNewestCandidateAsync(cancellationToken);
        var snapshot = candidate is null
            ? null
            : await _snapshotStore.GetAsync(candidate.SafetySnapshotId, cancellationToken);

        var capture = await _observationSource.CaptureAsync(cancellationToken);
        if (!capture.IsCaptured || capture.Observation is null)
        {
            var issues = capture.Issues.Count == 0
                ? new[] { "无法建立 fresh DesktopObservation。" }
                : capture.Issues.Select(issue => issue.Message).ToArray();
            return DesktopLayoutRollbackPreflightResult.ForCandidate(
                DesktopLayoutRollbackBlockReason.CurrentDesktopUnreadable,
                "读不到当前桌面 · 撤销入口保持关闭",
                string.Join("；", issues),
                candidate);
        }

        return DesktopLayoutRollbackPreflight.EvaluateCandidate(
            candidate,
            snapshot,
            capture.Observation.Icons,
            capture.Observation.Displays);
    }

    public async Task<DesktopLayoutRollbackResult> RollbackAsync(
        CancellationToken cancellationToken = default)
    {
        await _rollbackLock.WaitAsync(cancellationToken);
        try
        {
            await _snapshotStore.InitializeAsync(cancellationToken);
            await _recoveryStore.InitializeAsync(cancellationToken);

            // 1. 未收敛现场一律 fail-closed，与「应用到桌面」同一套 interlock。
            var unresolvedCandidates = await _recoveryStore.ListUnresolvedAsync(50, cancellationToken);
            var unresolved = unresolvedCandidates
                .Where(record => record.GetDisposition().IsUnresolved)
                .Take(1)
                .ToArray();
            if (unresolved.Length != 0)
            {
                return DesktopLayoutRollbackResult.Rejected(
                    DesktopLayoutRollbackOutcome.UnresolvedRecoveryBlocked,
                    "有未收敛的执行现场 · 撤销已暂停",
                    $"检测到未解决恢复会话 {unresolved[0].SessionId:D}。先点「处理恢复状态」把它收敛，再撤销。",
                    blockReason: DesktopLayoutRollbackBlockReason.UnresolvedRecoverySite);
            }

            if (_workspaceExecutionJournalStore is not null)
            {
                await _workspaceExecutionJournalStore.InitializeAsync(cancellationToken);
                var unresolvedWorkspace =
                    await _workspaceExecutionJournalStore.ListUnresolvedAsync(cancellationToken);
                if (unresolvedWorkspace.Count != 0)
                {
                    return DesktopLayoutRollbackResult.Rejected(
                        DesktopLayoutRollbackOutcome.UnresolvedRecoveryBlocked,
                        "有未收敛的工作区执行现场 · 撤销已暂停",
                        $"检测到未解决 V9 Workspace execution journal {unresolvedWorkspace[0].SessionId:D}。",
                        blockReason: DesktopLayoutRollbackBlockReason.UnresolvedRecoverySite);
                }
            }

            if (_dynamicWorkspaceExecutionJournalStore is not null)
            {
                await _dynamicWorkspaceExecutionJournalStore.InitializeAsync(cancellationToken);
                var unresolvedDynamic =
                    await _dynamicWorkspaceExecutionJournalStore.ListUnresolvedAsync(cancellationToken);
                if (unresolvedDynamic.Count != 0)
                {
                    return DesktopLayoutRollbackResult.Rejected(
                        DesktopLayoutRollbackOutcome.UnresolvedRecoveryBlocked,
                        "有未收敛的动态执行现场 · 撤销已暂停",
                        $"检测到未解决 Dynamic Workspace execution journal {unresolvedDynamic[0].SessionId:D}。",
                        blockReason: DesktopLayoutRollbackBlockReason.UnresolvedRecoverySite);
                }
            }

            // 2. 候选 + 安全检查点 + fresh 观察：一次观察同时供判定与计划使用，
            //    避免"判定依据"和"执行依据"来自两次不同事实。
            var candidate = await ResolveNewestCandidateAsync(cancellationToken);
            var snapshot = candidate is null
                ? null
                : await _snapshotStore.GetAsync(candidate.SafetySnapshotId, cancellationToken);

            var capture = await _observationSource.CaptureAsync(cancellationToken);
            if (!capture.IsCaptured || capture.Observation is null)
            {
                var issues = capture.Issues.Count == 0
                    ? new[] { "无法建立 fresh DesktopObservation。" }
                    : capture.Issues.Select(issue => issue.Message).ToArray();
                return DesktopLayoutRollbackResult.Rejected(
                    DesktopLayoutRollbackOutcome.PreflightBlocked,
                    "读不到当前桌面 · 撤销入口保持关闭",
                    string.Join("；", issues),
                    blockReason: DesktopLayoutRollbackBlockReason.CurrentDesktopUnreadable,
                    issues: issues);
            }

            var observation = capture.Observation;
            var preflight = DesktopLayoutRollbackPreflight.EvaluateCandidate(
                candidate,
                snapshot,
                observation.Icons,
                observation.Displays);
            if (!preflight.CanRollback)
            {
                return DesktopLayoutRollbackResult.Rejected(
                    DesktopLayoutRollbackOutcome.PreflightBlocked,
                    preflight.Headline,
                    preflight.Detail,
                    blockReason: preflight.Reason,
                    preflight: preflight,
                    preflight.ChangedItemNames.ToArray());
            }

            // ── 跨屏撤销 ──────────────────────────────────────────────────────────
            // 上次整理动过哪些屏，就要把哪些屏分别还原回去：每个屏各自按同一份检查点快照
            // 生成还原计划、各自过同一道安全门、各自走同一套受控执行（同一 Coordinator，
            // 它在每笔写入前再存并回读一次安全检查点）。
            // 任何一屏排不出计划、被门拒绝或执行失败，都立刻停在这里如实报告，后面的屏不再动。
            var restoreTargets = DesktopLayoutRollbackRestoreTargetResolver.Resolve(candidate, snapshot!);
            if (restoreTargets.ItemsMissingFromSnapshot.Count != 0)
            {
                return DesktopLayoutRollbackResult.Rejected(
                    DesktopLayoutRollbackOutcome.PlanRejected,
                    "安全检查点里找不到这次整理动过的项目",
                    $"有 {restoreTargets.ItemsMissingFromSnapshot.Count} 个项目（例如 {restoreTargets.ItemsMissingFromSnapshot[0]}）"
                    + "不在检查点里，没法知道它们原来在哪儿，撤销停在这里。",
                    blockReason: DesktopLayoutRollbackBlockReason.RestorePlanBlocked,
                    preflight: preflight);
            }

            if (restoreTargets.ItemsWithoutDisplay.Count != 0)
            {
                return DesktopLayoutRollbackResult.Rejected(
                    DesktopLayoutRollbackOutcome.PlanRejected,
                    "有些项目在检查点里没有显示器归属",
                    $"有 {restoreTargets.ItemsWithoutDisplay.Count} 个项目（例如 {restoreTargets.ItemsWithoutDisplay[0]}）"
                    + "在检查点里没有记录属于哪块屏，撤销停在这里。",
                    blockReason: DesktopLayoutRollbackBlockReason.RestorePlanBlocked,
                    preflight: preflight);
            }

            if (restoreTargets.DeviceNames.Count == 0)
            {
                return DesktopLayoutRollbackResult.Rejected(
                    DesktopLayoutRollbackOutcome.PreflightBlocked,
                    "现在就是整理前的样子",
                    "按安全检查点比对，没有需要挪回去的项目。",
                    blockReason: DesktopLayoutRollbackBlockReason.NothingToUndo,
                    preflight: preflight);
            }

            var executions = new List<TargetDisplayLayoutExecutionRunResult>();
            var executedDevices = new List<string>();

            // 中途停住时的收尾：一屏都没动过就保持"桌面未动"的措辞；
            // 已经动过就把"还原了哪几屏"如实写进说明。
            DesktopLayoutRollbackResult Stop(
                DesktopLayoutRollbackOutcome outcome,
                string headline,
                string detail,
                DesktopLayoutRollbackBlockReason blockReason,
                IReadOnlyList<string>? issues = null)
            {
                if (executions.Count == 0)
                {
                    return DesktopLayoutRollbackResult.Rejected(
                        outcome,
                        headline,
                        detail,
                        blockReason: blockReason,
                        preflight: preflight,
                        (issues ?? Array.Empty<string>()).ToArray());
                }

                return DesktopLayoutRollbackResult.FromExecutions(
                    executions,
                    preflight,
                    executedDevices,
                    partialNote: $"{headline}（已还原 {string.Join("、", executedDevices)}）",
                    extraIssues: issues);
            }

            for (var index = 0; index < restoreTargets.DeviceNames.Count; index++)
            {
                var targetDeviceName = restoreTargets.DeviceNames[index];

                // 第一块屏复用判定用的那次 fresh 观察（判定与计划同源），其余屏各自再读一次。
                if (index != 0)
                    capture = await _observationSource.CaptureAsync(cancellationToken);
                if (!capture.IsCaptured || capture.Observation is null)
                {
                    var readIssues = capture.Issues.Count == 0
                        ? new[] { "无法建立 fresh DesktopObservation。" }
                        : capture.Issues.Select(issue => issue.Message).ToArray();
                    return Stop(
                        DesktopLayoutRollbackOutcome.ExecutionFailed,
                        "读不到当前桌面 · 撤销停在这里",
                        $"{targetDeviceName} 这一屏读不到当前桌面，剩下的屏不再继续。",
                        DesktopLayoutRollbackBlockReason.CurrentDesktopUnreadable,
                        readIssues);
                }

                var targetObservation = capture.Observation;
                var targetReadResult = new DesktopIconReadResult(
                    targetObservation.Icons,
                    FailedItemCount: 0,
                    targetObservation.IconReadConnectionMethod,
                    targetObservation.IconReadTime,
                    PrimaryPathException: null);

                TargetDisplayLayoutRestorePlan restorePlan;
                try
                {
                    restorePlan = TargetDisplayLayoutRestorePlanner.CreatePlan(
                        snapshot!,
                        targetReadResult,
                        targetObservation.Displays,
                        targetObservation.PositioningCapabilities,
                        targetDeviceName,
                        allowSnapshotFromAnotherDisplay: true);
                }
                catch (Exception ex)
                {
                    return Stop(
                        DesktopLayoutRollbackOutcome.PlanRejected,
                        "按安全检查点生成还原计划失败",
                        $"{targetDeviceName}：{ex.GetType().Name}: {ex.Message}。桌面未动。",
                        DesktopLayoutRollbackBlockReason.RestorePlanBlocked);
                }

                if (restorePlan.IsNoOp || restorePlan.MoveRequiredCount == 0)
                    continue;

                if (restorePlan.HasBlockingErrors || restorePlan.Cycles.Count > 0 || restorePlan.MissingCount > 0)
                {
                    var reasons = new List<string>();
                    reasons.AddRange(restorePlan.Issues.Select(issue => $"[{issue.Code}] {issue.Message}"));
                    reasons.AddRange(restorePlan.Items
                        .Where(item => item.BlockReason is not null)
                        .Select(item => $"[{item.BlockReason}] {item.DisplayName}"));
                    if (restorePlan.MissingCount > 0)
                        reasons.Add($"{restorePlan.MissingCount} 个项目已经不在当前桌面上。");
                    if (restorePlan.Cycles.Count > 0)
                        reasons.Add($"计划里有 {restorePlan.Cycles.Count} 处互相挡路（环形依赖）。");

                    return Stop(
                        DesktopLayoutRollbackOutcome.PlanRejected,
                        "还原计划被挡下了 · 这一屏没有移动图标",
                        $"{targetDeviceName}：有项目被别的图标占住、互相挡路或已经不在桌面上。"
                        + "撤销停在这里，你可以手工处理后再试。",
                        DesktopLayoutRollbackBlockReason.RestorePlanBlocked,
                        reasons.Take(5).ToArray());
                }

                var executionPlan = TargetDisplayLayoutExecutionPlanner.CreatePlan(
                    restorePlan,
                    targetReadResult,
                    targetObservation.Displays,
                    targetObservation.PositioningCapabilities);
                var usesTemporaryStaging = executionPlan.UsesTemporaryStaging;
                var gateResult = SelectAuthorizedScopeGate(usesTemporaryStaging)(restorePlan, executionPlan);
                if (!gateResult.IsAllowed)
                {
                    return Stop(
                        DesktopLayoutRollbackOutcome.ScopeRejected,
                        "撤销被安全门拒绝",
                        $"{targetDeviceName}：" + (usesTemporaryStaging
                            ? "这次还原需要临时中转，而当前只允许直接移动。桌面未动。"
                            : "这次还原不满足直接批量移动的条件。桌面未动。"),
                        DesktopLayoutRollbackBlockReason.ScopeNotAllowed,
                        gateResult.RejectionReasons.ToArray());
                }

                // 3. 交给 Coordinator：它会再读一次桌面、重建 ExecutionPlan、再跑同一道门，
                //    并在第一笔写入前保存并回读安全检查点。
                var executionResult = await _executeAuthorizedAsync(
                    restorePlan,
                    usesTemporaryStaging,
                    CancellationToken.None);

                executions.Add(executionResult);
                executedDevices.Add(targetDeviceName);

                if (!executionResult.IsSuccess)
                {
                    return DesktopLayoutRollbackResult.FromExecutions(
                        executions,
                        preflight,
                        executedDevices,
                        partialNote: executedDevices.Count > 1
                            ? $"已还原 {string.Join("、", executedDevices.Take(executedDevices.Count - 1))}，"
                              + $"{targetDeviceName} 这一屏没有完成。"
                            : null);
                }
            }

            if (executions.Count == 0)
            {
                return DesktopLayoutRollbackResult.Rejected(
                    DesktopLayoutRollbackOutcome.PreflightBlocked,
                    "现在就是整理前的样子",
                    "按安全检查点比对，没有需要挪回去的项目。",
                    blockReason: DesktopLayoutRollbackBlockReason.NothingToUndo,
                    preflight: preflight);
            }

            return DesktopLayoutRollbackResult.FromExecutions(executions, preflight, executedDevices);
        }
        finally
        {
            _rollbackLock.Release();
        }
    }

    /// <summary>
    /// 「最近一次成功整理」的候选：两条执行路径各取最新一条已收尾写入，按开始时间取更晚的那条。
    /// 「应用到桌面」写恢复会话；工作区设计应用只写安全检查点 + 动态执行 journal —— 两条都要看得见，
    /// 否则用户撤不掉其中一条，而且会错误地退回到更早的那条。
    /// </summary>
    private async Task<DesktopLayoutRollbackCandidate?> ResolveNewestCandidateAsync(
        CancellationToken cancellationToken)
    {
        var session = (await _committedSessions.ListCommittedAsync(1, cancellationToken)).FirstOrDefault();
        var newest = DesktopLayoutRollbackCandidate.TryFromRecoverySession(session);

        if (_committedWorkspaceWrites is not null)
        {
            var journals = await _committedWorkspaceWrites.ListCommittedAsync(1, cancellationToken);
            var workspace = DesktopLayoutRollbackCandidate.TryFromWorkspaceApply(journals.FirstOrDefault());
            if (workspace is not null && (newest is null || workspace.StartedAtUtc > newest.StartedAtUtc))
                newest = workspace;
        }

        return newest;
    }

    private static Func<
        TargetDisplayLayoutRestorePlan,
        TargetDisplayLayoutExecutionPlan,
        TargetDisplayLayoutExecutionScopeGate.GateResult> SelectAuthorizedScopeGate(
        bool usesTemporaryStaging)
        => usesTemporaryStaging
            ? TargetDisplayLayoutStagingExecutionScopeGate.Evaluate
            : TargetDisplayLayoutExecutionScopeGate.EvaluateNoStagingBatch;
}
