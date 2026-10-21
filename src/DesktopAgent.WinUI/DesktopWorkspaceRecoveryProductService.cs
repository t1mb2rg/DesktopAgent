using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;

namespace DesktopAgent.WinUI;

internal enum DesktopWorkspaceRecoveryProductOutcome
{
    NoPendingRecovery = 0,
    MultiplePendingRecoveries = 1,
    NotAcceptable = 2,
    AcceptedCurrentState = 3,
}

internal sealed record DesktopWorkspaceRecoveryProductResult(
    DesktopWorkspaceRecoveryProductOutcome Outcome,
    int PendingCount,
    Guid? SessionId,
    string? Message);

/// <summary>
/// Workspace NeedsAttention 的产品级“接受当前桌面”入口。
/// 它没有 Writer / Observation / Planner authority，只允许用户明确结束 unresolved execution lock，
/// 并保留原 Lifecycle、Safety Snapshot、Execution Plan 与全部 Writer attempt 证据。
/// </summary>
internal sealed class DesktopWorkspaceRecoveryProductService
{
    private readonly IDesktopWorkspaceExecutionJournalStore _journalStore;

    internal DesktopWorkspaceRecoveryProductService(
        IDesktopWorkspaceExecutionJournalStore journalStore)
        => _journalStore = journalStore ?? throw new ArgumentNullException(nameof(journalStore));

    internal async Task<DesktopWorkspaceRecoveryProductResult> AcceptCurrentStateSingleAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _journalStore.InitializeAsync(cancellationToken);
        var pending = await _journalStore.ListUnresolvedAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return new(
                DesktopWorkspaceRecoveryProductOutcome.NoPendingRecovery,
                0,
                null,
                "当前没有 unresolved Workspace execution journal。");
        }

        if (pending.Count != 1)
        {
            return new(
                DesktopWorkspaceRecoveryProductOutcome.MultiplePendingRecoveries,
                pending.Count,
                null,
                "存在多笔 Workspace execution journal，当前版本不猜测裁决顺序。");
        }

        var current = pending[0];
        if (current.Lifecycle != DesktopWorkspaceExecutionJournalLifecycle.NeedsAttention)
        {
            return new(
                DesktopWorkspaceRecoveryProductOutcome.NotAcceptable,
                1,
                current.SessionId,
                $"当前 Workspace journal 状态为 {current.Lifecycle}，只允许对 NeedsAttention 现场接受当前桌面。");
        }

        var requested = current with
        {
            RecoveryFailureType = DesktopWorkspaceExecutionResolutionMarkers.UserAcceptedCurrentState,
            RecoveryFailureMessage =
                "用户明确接受当前真实桌面作为新的现实基线；未宣称 Workspace Apply 成功，也未宣称已恢复到 Safety Snapshot。",
        };
        var updated = await _journalStore.UpdateAsync(requested, cancellationToken);
        var readBack = await _journalStore.GetAsync(updated.SessionId, cancellationToken)
            ?? throw new InvalidDataException("Workspace current-state acceptance 保存后回读返回 null。");

        if (!DesktopWorkspaceExecutionResolutionMarkers.IsUserAcceptedCurrentState(readBack))
            throw new InvalidDataException("Workspace current-state acceptance 回读后缺少用户裁决 marker。");

        var remaining = await _journalStore.ListUnresolvedAsync(cancellationToken);
        if (remaining.Any(journal => journal.SessionId == readBack.SessionId))
            throw new InvalidDataException("用户已接受当前桌面，但该 Workspace journal 仍然出现在 unresolved 列表中。");

        return new(
            DesktopWorkspaceRecoveryProductOutcome.AcceptedCurrentState,
            remaining.Count,
            readBack.SessionId,
            "当前桌面已由用户明确接受为新的现实基线；原失败证据仍保留。下一次 Apply 必须重新生成 fresh Preview。");
    }
}
