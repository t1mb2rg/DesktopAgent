using DesktopAgent.Application.DesktopLayouts.Templates.Editing;

namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    /// <summary>
    /// Direct Apply 已经由 Coordinator + independent verification 证明成功后，
    /// 把相应 pending migration 提交为稳定工作区状态。
    /// 这里只更新内存 Draft/意图，不获得 Writer authority。
    /// </summary>
    private void CommitSuccessfulCrossDisplayMigrations(
        string targetDeviceName,
        IReadOnlyCollection<string> completedStableIds)
    {
        if (!IsAiMultiDisplayWorkspaceActive
            || _aiWorkspaceMigrations.Count == 0
            || completedStableIds.Count == 0)
        {
            return;
        }

        var matching = completedStableIds
            .Where(stableId => _aiWorkspaceMigrations.Any(item =>
                string.Equals(item.StableId, stableId, StringComparison.Ordinal)
                && string.Equals(item.TargetDeviceName, targetDeviceName, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (matching.Length == 0)
            return;

        // 把当前编辑器里的最后状态先写回 workspace，防止提交迁移时覆盖用户刚刚确认过的草稿。
        PersistCurrentAiWorkspaceDraftFromEditor();

        var committed = DesktopCrossDisplayWorkspaceEditor.CompleteTargetMigrations(
            _aiWorkspaceDrafts.Select(static state => state.Draft).ToArray(),
            _aiWorkspaceMigrations,
            targetDeviceName,
            matching);

        var draftByDevice = committed.Drafts.ToDictionary(
            static draft => draft.TargetDisplayBinding.DeviceName!,
            StringComparer.Ordinal);
        foreach (var state in _aiWorkspaceDrafts)
        {
            if (!draftByDevice.TryGetValue(state.DeviceName, out var draft))
                throw new InvalidOperationException(
                    $"跨屏迁移提交结果缺少显示器 {state.DeviceName} 的 Draft。");
            state.Draft = draft;
        }

        _aiWorkspaceMigrations.Clear();
        _aiWorkspaceMigrations.AddRange(committed.Migrations);

        if (IsAiMultiDisplayWorkspaceActive)
            _draft = _aiWorkspaceDrafts[_activeAiWorkspaceIndex].Draft;

        var recoveryError = TryPersistAiMultiDisplayWorkspace();
        if (recoveryError is not null)
            LayoutCanvasStatusText.Text = $"跨屏迁移已完成，但多屏草稿恢复状态未保存：{recoveryError}";
    }
}
