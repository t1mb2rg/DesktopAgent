using DesktopAgent.Core.DesktopIcons;
using DesktopAgent.Core.DesktopLayouts;
using DesktopAgent.Core.DesktopLayouts.Restore;
using DesktopAgent.Core.DesktopLayouts.Restore.Execution;
using DesktopAgent.Core.Displays;
using DesktopAgent.Core.Geometry;

namespace DesktopAgent.Core.DesktopLayouts.Templates.Execution;

/// <summary>
/// 执行后独立验证器。纯逻辑，不依赖 COM/PIDL/HWND。
/// 使用独立的完整桌面重读来验证执行结果。
/// </summary>
public static class DesktopLayoutPostExecutionVerifier
{
    /// <summary>
    /// 单图标验证证据。
    /// </summary>
    public sealed record IconVerificationEvidence
    {
        public string StableId { get; }
        public bool Found { get; }
        public PixelPoint? ExpectedPosition { get; }
        public PixelPoint? ActualPosition { get; }
        public string? ExpectedMonitorDeviceName { get; }
        public string? ActualMonitorDeviceName { get; }
        public bool PositionMatches { get; }
        public bool MonitorMatches { get; }
        public string? Note { get; }

        public bool Passed => Found && PositionMatches && MonitorMatches;

        public IconVerificationEvidence(
            string stableId, bool found,
            PixelPoint? expectedPosition, PixelPoint? actualPosition,
            string? expectedMonitorDeviceName, string? actualMonitorDeviceName,
            bool positionMatches, bool monitorMatches, string? note)
        {
            StableId = stableId;
            Found = found;
            ExpectedPosition = expectedPosition;
            ActualPosition = actualPosition;
            ExpectedMonitorDeviceName = expectedMonitorDeviceName;
            ActualMonitorDeviceName = actualMonitorDeviceName;
            PositionMatches = positionMatches;
            MonitorMatches = monitorMatches;
            Note = note;
        }
    }

    /// <summary>
    /// 批量验证结果聚合。
    /// </summary>
    public sealed record BatchVerificationResult
    {
        public VerificationPhase Phase { get; }
        public VerificationOutcome Outcome { get; }
        public int TotalChecked { get; }
        public int PassedCount { get; }
        public int FailedCount { get; }
        public int InconclusiveCount { get; }
        public IReadOnlyList<IconVerificationEvidence> Evidences { get; }
        public IReadOnlyList<string> GlobalIssues { get; }
        public string Summary { get; }

        public bool AllPassed => Outcome == VerificationOutcome.Passed;

        public BatchVerificationResult(
            VerificationPhase phase,
            VerificationOutcome outcome,
            int totalChecked, int passedCount, int failedCount, int inconclusiveCount,
            IReadOnlyList<IconVerificationEvidence> evidences,
            IReadOnlyList<string> globalIssues,
            string summary)
        {
            Phase = phase;
            Outcome = outcome;
            TotalChecked = totalChecked;
            PassedCount = passedCount;
            FailedCount = failedCount;
            InconclusiveCount = inconclusiveCount;
            Evidences = evidences;
            GlobalIssues = globalIssues;
            Summary = summary;
        }

        public static BatchVerificationResult NotNeeded(string reason) =>
            new(VerificationPhase.None, VerificationOutcome.NotNeeded,
                0, 0, 0, 0,
                Array.Empty<IconVerificationEvidence>(),
                new[] { reason }, reason);

        public static BatchVerificationResult Inconclusive(
            VerificationPhase phase, string reason,
            IReadOnlyList<IconVerificationEvidence>? evidences = null) =>
            new(phase, VerificationOutcome.Inconclusive,
                evidences?.Count ?? 0, 0, 0, evidences?.Count ?? 0,
                evidences ?? Array.Empty<IconVerificationEvidence>(),
                new[] { reason }, reason);

        public static BatchVerificationResult Passed(
            VerificationPhase phase, IReadOnlyList<IconVerificationEvidence> evidences) =>
            new(phase, VerificationOutcome.Passed,
                evidences.Count, evidences.Count, 0, 0,
                evidences, Array.Empty<string>(),
                $"全部 {evidences.Count} 项独立验证通过。");

        public static BatchVerificationResult Failed(
            VerificationPhase phase, IReadOnlyList<IconVerificationEvidence> evidences,
            string summary, IReadOnlyList<string>? issues = null)
        {
            var passed = evidences.Count(e => e.Passed);
            var failed = evidences.Count(e => e.Found && !e.Passed);
            var inconclusive = evidences.Count(e => !e.Found);
            return new(phase, VerificationOutcome.Failed,
                evidences.Count, passed, failed, inconclusive,
                evidences, issues ?? Array.Empty<string>(), summary);
        }
    }

    public enum VerificationPhase { None, Forward, Rollback }
    public enum VerificationOutcome { NotNeeded, Passed, Failed, Inconclusive }

    /// <summary>
    /// 基于执行结果和独立桌面重读进行验证。
    /// </summary>
    /// <param name="runResult">Coordinator 返回的执行结果。</param>
    /// <param name="freshReadResult">独立完整桌面重读结果。</param>
    /// <param name="displays">当前显示器拓扑。</param>
    /// <param name="safetySnapshot">执行前保存的安全快照（用于回滚验证）。</param>
    public static BatchVerificationResult Verify(
        TargetDisplayLayoutExecutionRunResult runResult,
        DesktopIconReadResult freshReadResult,
        IReadOnlyList<DisplayDescriptor> displays,
        DesktopLayoutSnapshot? safetySnapshot)
    {
        if (runResult is null) throw new ArgumentNullException(nameof(runResult));
        if (freshReadResult is null) throw new ArgumentNullException(nameof(freshReadResult));
        if (displays is null) throw new ArgumentNullException(nameof(displays));

        // ═══ 前置检查 ═══
        if (freshReadResult.FailedItemCount > 0)
        {
            return BatchVerificationResult.Inconclusive(
                VerificationPhase.None,
                $"独立重读不完整：{freshReadResult.FailedItemCount} 个图标读取失败。" +
                "无法完成独立验证。");
        }

        var globalIssues = new List<string>();

        // StableId 重复检查
        var sidCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var icon in freshReadResult.Icons)
        {
            sidCounts.TryGetValue(icon.StableId, out var c);
            sidCounts[icon.StableId] = c + 1;
        }
        var duplicateSids = sidCounts.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();
        if (duplicateSids.Count > 0)
        {
            globalIssues.Add($"独立重读中存在重复 StableId：{string.Join(", ", duplicateSids)}。" +
                "独立验证结果不可信。");
        }

        return runResult.Status switch
        {
            TargetDisplayLayoutExecutionRunStatus.Completed =>
                VerifyForward(runResult, freshReadResult, displays, globalIssues),

            TargetDisplayLayoutExecutionRunStatus.FailedAndRolledBack =>
                VerifyRollback(runResult, freshReadResult, displays, safetySnapshot, globalIssues),

            TargetDisplayLayoutExecutionRunStatus.CompletedNoOp =>
                BatchVerificationResult.NotNeeded("NoOp — 未发生写入。"),

            TargetDisplayLayoutExecutionRunStatus.FailedBeforeWrite =>
                BatchVerificationResult.NotNeeded("FailedBeforeWrite — Writer 未启动。"),

            TargetDisplayLayoutExecutionRunStatus.PositionUncertain =>
                BatchVerificationResult.Inconclusive(
                    VerificationPhase.None,
                    "PositionUncertain — 位置不可信，需要人工处理。" +
                    "独立验证无法替代人工确认。"),

            TargetDisplayLayoutExecutionRunStatus.FailedRollbackIncomplete =>
                BatchVerificationResult.Inconclusive(
                    VerificationPhase.Rollback,
                    "FailedRollbackIncomplete — 回滚不完整，需要人工处理。" +
                    "独立验证无法替代人工确认。"),

            _ => BatchVerificationResult.Inconclusive(
                VerificationPhase.None,
                $"未知状态 {runResult.Status}。")
        };
    }

    /// <summary>
    /// 正向验证：所有 MoveRequired 图标必须在目标位置和目标显示器。
    /// 同时验证 AlreadyAtTarget 图标未被外部移动（完整重读证据）。
    /// </summary>
    private static BatchVerificationResult VerifyForward(
        TargetDisplayLayoutExecutionRunResult runResult,
        DesktopIconReadResult freshRead,
        IReadOnlyList<DisplayDescriptor> displays,
        List<string> globalIssues)
    {
        var session = runResult.Session;
        if (session is null)
        {
            return BatchVerificationResult.Inconclusive(
                VerificationPhase.Forward,
                "Completed 但 RecoverySession 为 null，无法执行独立验证。");
        }

        var restorePlan = session.SourceRestorePlan;
        var evidences = new List<IconVerificationEvidence>();

        foreach (var item in restorePlan.Items)
        {
            if (item.State == TargetDisplayLayoutRestoreItemState.MoveRequired)
            {
                // 验证 MoveRequired 项已到达目标位置
                var freshIcon = FindIcon(freshRead, item.StableId);

                if (freshIcon is null)
                {
                    evidences.Add(new IconVerificationEvidence(
                        item.StableId, false,
                        item.SnapshotTargetPosition, null,
                        restorePlan.TargetDeviceName, null,
                        false, false,
                        $"MoveRequired StableId \"{item.StableId}\" 在独立重读中未找到。"));
                    continue;
                }

                var posMatch = freshIcon.PixelPosition == item.SnapshotTargetPosition;
                var monMatch = string.Equals(
                    freshIcon.MonitorDeviceName ?? "",
                    restorePlan.TargetDeviceName,
                    StringComparison.Ordinal);

                var note = BuildPositionNote(posMatch, monMatch,
                    item.SnapshotTargetPosition, freshIcon.PixelPosition,
                    restorePlan.TargetDeviceName, freshIcon.MonitorDeviceName,
                    "MoveRequired");

                evidences.Add(new IconVerificationEvidence(
                    item.StableId, true,
                    item.SnapshotTargetPosition, freshIcon.PixelPosition,
                    restorePlan.TargetDeviceName, freshIcon.MonitorDeviceName,
                    posMatch, monMatch, note));
            }
            else if (item.State == TargetDisplayLayoutRestoreItemState.AlreadyAtTarget
                     && item.CurrentPosition.HasValue)
            {
                // 验证 AlreadyAtTarget 项未被外部移动
                var freshIcon = FindIcon(freshRead, item.StableId);

                if (freshIcon is null)
                {
                    evidences.Add(new IconVerificationEvidence(
                        item.StableId, false,
                        item.CurrentPosition.Value, null,
                        item.CurrentMonitorDeviceName, null,
                        false, false,
                        $"AlreadyAtTarget StableId \"{item.StableId}\" 在独立重读中未找到。" +
                        "可能在执行期间被删除。"));
                    continue;
                }

                var posMatch = freshIcon.PixelPosition == item.CurrentPosition.Value;
                var monMatch = string.Equals(
                    freshIcon.MonitorDeviceName ?? "",
                    item.CurrentMonitorDeviceName ?? "",
                    StringComparison.Ordinal);

                var note = BuildPositionNote(posMatch, monMatch,
                    item.CurrentPosition.Value, freshIcon.PixelPosition,
                    item.CurrentMonitorDeviceName ?? "", freshIcon.MonitorDeviceName,
                    "AlreadyAtTarget");

                evidences.Add(new IconVerificationEvidence(
                    item.StableId, true,
                    item.CurrentPosition.Value, freshIcon.PixelPosition,
                    item.CurrentMonitorDeviceName, freshIcon.MonitorDeviceName,
                    posMatch, monMatch, note));
            }
        }

        if (evidences.Count == 0)
        {
            return BatchVerificationResult.Passed(VerificationPhase.Forward, evidences);
        }

        var allPassed = evidences.TrueForAll(e => e.Passed);
        if (allPassed)
        {
            if (globalIssues.Count > 0)
            {
                return BatchVerificationResult.Failed(
                    VerificationPhase.Forward, evidences,
                    $"独立验证发现 {globalIssues.Count} 个全局问题。",
                    globalIssues);
            }

            return BatchVerificationResult.Passed(VerificationPhase.Forward, evidences);
        }

        var passed = evidences.Count(e => e.Passed);
        var failed = evidences.Count(e => e.Found && !e.Passed);
        var missing = evidences.Count(e => !e.Found);

        return BatchVerificationResult.Failed(
            VerificationPhase.Forward, evidences,
            $"独立验证失败：{passed} 通过，{failed} 不匹配，{missing} 缺失。" +
            (globalIssues.Count > 0
                ? $" 全局问题：{string.Join("; ", globalIssues)}"
                : ""),
            globalIssues);
    }

    private static string? BuildPositionNote(
        bool posMatch, bool monMatch,
        PixelPoint expectedPos, PixelPoint actualPos,
        string expectedMon, string? actualMon,
        string itemKind)
    {
        return (posMatch, monMatch) switch
        {
            (true, true) => null,
            (false, true) => $"{itemKind} 位置不匹配：期望 ({expectedPos.X}," +
                $"{expectedPos.Y})，实际 ({actualPos.X},{actualPos.Y})。",
            (true, false) => $"{itemKind} 显示器不匹配：期望 {expectedMon}，" +
                $"实际 {actualMon}。",
            (false, false) => $"{itemKind} 位置和显示器均不匹配。" +
                $"位置期望 ({expectedPos.X},{expectedPos.Y})，" +
                $"实际 ({actualPos.X},{actualPos.Y})；" +
                $"显示器期望 {expectedMon}，实际 {actualMon}。",
        };
    }

    /// <summary>
    /// 回滚验证：所有已回滚图标必须在原始位置和原始显示器。
    /// </summary>
    private static BatchVerificationResult VerifyRollback(
        TargetDisplayLayoutExecutionRunResult runResult,
        DesktopIconReadResult freshRead,
        IReadOnlyList<DisplayDescriptor> displays,
        DesktopLayoutSnapshot? safetySnapshot,
        List<string> globalIssues)
    {
        var session = runResult.Session;
        if (session is null)
        {
            return BatchVerificationResult.Inconclusive(
                VerificationPhase.Rollback,
                "FailedAndRolledBack 但 RecoverySession 为 null，无法执行独立验证。");
        }

        var evidences = new List<IconVerificationEvidence>();

        // 从 SafetySnapshot 获取原始位置（如果可用）
        Dictionary<string, (PixelPoint Position, string? Monitor)>? originalPositions = null;
        if (safetySnapshot is not null)
        {
            originalPositions = new Dictionary<string, (PixelPoint, string?)>(StringComparer.Ordinal);
            foreach (var icon in safetySnapshot.Icons)
            {
                originalPositions[icon.StableId] = (icon.PixelPosition, icon.MonitorDeviceName);
            }
        }

        // 验证所有被回滚的步骤
        var rolledBackSids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rb in runResult.RollbackAttempts)
        {
            if (rb.Outcome != TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
                continue;
            rolledBackSids.Add(rb.ExpectedStableId);
        }

        if (rolledBackSids.Count == 0)
        {
            // 回滚结果为空但仍声称 FailedAndRolledBack — 内部不一致
            return BatchVerificationResult.Failed(
                VerificationPhase.Rollback,
                Array.Empty<IconVerificationEvidence>(),
                "FailedAndRolledBack 但无成功回滚记录，RunResult 内部不一致。",
                globalIssues);
        }

        // 同时也检查正向步骤中 ExactSuccess 的（它们在回滚中已被还原）
        foreach (var fwd in runResult.ForwardAttempts)
        {
            if (fwd.Outcome == TargetDisplayLayoutExecutionAttemptOutcome.ExactSuccess)
                rolledBackSids.Add(fwd.ExpectedStableId);
        }

        foreach (var sid in rolledBackSids)
        {
            var freshIcon = FindIcon(freshRead, sid);

            if (freshIcon is null)
            {
                evidences.Add(new IconVerificationEvidence(
                    sid, false, null, null, null, null, false, false,
                    $"StableId \"{sid}\" 在独立重读中未找到。"));
                continue;
            }

            // 尝试从 safety snapshot 找原始位置
            PixelPoint? expectedPos = null;
            string? expectedMon = null;
            if (originalPositions is not null && originalPositions.TryGetValue(sid, out var orig))
            {
                expectedPos = orig.Position;
                expectedMon = orig.Monitor;
            }

            bool posMatch, monMatch;
            string? note;

            if (expectedPos.HasValue)
            {
                posMatch = freshIcon.PixelPosition == expectedPos.Value;
                monMatch = string.Equals(
                    freshIcon.MonitorDeviceName ?? "",
                    expectedMon ?? "",
                    StringComparison.Ordinal);

                note = (posMatch, monMatch) switch
                {
                    (true, true) => null,
                    (false, true) => $"回滚位置不匹配：期望原位置 ({expectedPos.Value.X}," +
                        $"{expectedPos.Value.Y})，实际 ({freshIcon.PixelPosition.X}," +
                        $"{freshIcon.PixelPosition.Y})。",
                    (true, false) => $"回滚显示器不匹配：期望 {expectedMon}，" +
                        $"实际 {freshIcon.MonitorDeviceName}。",
                    (false, false) => $"回滚位置和显示器均不匹配。" +
                        $"位置期望 ({expectedPos.Value.X},{expectedPos.Value.Y})，" +
                        $"实际 ({freshIcon.PixelPosition.X},{freshIcon.PixelPosition.Y})；" +
                        $"显示器期望 {expectedMon}，实际 {freshIcon.MonitorDeviceName}。",
                };
            }
            else
            {
                // 无安全快照原始位置 — 不能做精确验证
                posMatch = false;
                monMatch = false;
                note = "无安全快照原始位置数据，无法验证回滚精确性。";
            }

            evidences.Add(new IconVerificationEvidence(
                sid, true,
                expectedPos, freshIcon.PixelPosition,
                expectedMon, freshIcon.MonitorDeviceName,
                posMatch, monMatch, note));
        }

        var allPassed = evidences.TrueForAll(e => e.Passed);
        if (allPassed)
        {
            if (globalIssues.Count > 0)
            {
                return BatchVerificationResult.Failed(
                    VerificationPhase.Rollback, evidences,
                    $"独立验证发现 {globalIssues.Count} 个全局问题。",
                    globalIssues);
            }
            return BatchVerificationResult.Passed(VerificationPhase.Rollback, evidences);
        }

        var passed = evidences.Count(e => e.Passed);
        var failed = evidences.Count(e => e.Found && !e.Passed);
        var missing = evidences.Count(e => !e.Found);

        return BatchVerificationResult.Failed(
            VerificationPhase.Rollback, evidences,
            $"回滚独立验证失败：{passed} 通过，{failed} 不匹配，{missing} 缺失。" +
            (globalIssues.Count > 0
                ? $" 全局问题：{string.Join("; ", globalIssues)}"
                : ""),
            globalIssues);
    }

    private static DesktopIconDescriptor? FindIcon(
        DesktopIconReadResult readResult, string stableId)
    {
        DesktopIconDescriptor? found = null;
        var count = 0;
        foreach (var icon in readResult.Icons)
        {
            if (string.Equals(icon.StableId, stableId, StringComparison.Ordinal))
            {
                found = icon;
                count++;
            }
        }
        // 多个匹配视为未找到（歧义）
        return count == 1 ? found : null;
    }
}
