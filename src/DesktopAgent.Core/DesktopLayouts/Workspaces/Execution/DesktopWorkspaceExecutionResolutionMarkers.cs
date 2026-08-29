namespace DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;

/// <summary>
/// Workspace execution journal 的显式用户裁决标记。
/// Lifecycle 继续保留真实执行终态（例如 NeedsAttention），这些 marker 只表示用户随后如何处理该现场。
/// </summary>
public static class DesktopWorkspaceExecutionResolutionMarkers
{
    public const string UserAcceptedCurrentState = "UserAcceptedCurrentState";

    public static bool IsUserAcceptedCurrentState(DesktopWorkspaceExecutionJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        return journal.Lifecycle == DesktopWorkspaceExecutionJournalLifecycle.NeedsAttention &&
               string.Equals(
                   journal.RecoveryFailureType,
                   UserAcceptedCurrentState,
                   StringComparison.Ordinal);
    }
}
