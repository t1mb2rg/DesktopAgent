using DesktopAgent.Core.DesktopLayouts.Workspaces.Execution;

namespace DesktopAgent.Persistence.DesktopLayouts.Workspaces.Execution;

/// <summary>
/// 在不改写历史 Lifecycle 的前提下解释用户后续裁决。
/// NeedsAttention journal 仍完整保留，但用户明确接受当前桌面后不再阻塞新的执行。
/// </summary>
public sealed class AcceptedAwareDesktopWorkspaceExecutionJournalStore : IDesktopWorkspaceExecutionJournalStore
{
    private readonly SqliteDesktopWorkspaceExecutionJournalStore _inner;

    public AcceptedAwareDesktopWorkspaceExecutionJournalStore(string databasePath)
        => _inner = new SqliteDesktopWorkspaceExecutionJournalStore(databasePath);

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => _inner.InitializeAsync(cancellationToken);

    public Task SaveAsync(
        DesktopWorkspaceExecutionJournal journal,
        CancellationToken cancellationToken = default)
        => _inner.SaveAsync(journal, cancellationToken);

    public Task<DesktopWorkspaceExecutionJournal> UpdateAsync(
        DesktopWorkspaceExecutionJournal journal,
        CancellationToken cancellationToken = default)
        => _inner.UpdateAsync(journal, cancellationToken);

    public Task<DesktopWorkspaceExecutionJournal?> GetAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
        => _inner.GetAsync(sessionId, cancellationToken);

    public async Task<IReadOnlyList<DesktopWorkspaceExecutionJournal>> ListUnresolvedAsync(
        CancellationToken cancellationToken = default)
    {
        var journals = await _inner.ListUnresolvedAsync(cancellationToken).ConfigureAwait(false);
        return Array.AsReadOnly(journals
            .Where(static journal =>
                !DesktopWorkspaceExecutionResolutionMarkers.IsUserAcceptedCurrentState(journal))
            .ToArray());
    }
}
