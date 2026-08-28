namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    /// <summary>
    /// 只有新的 legacy PlanningSession 已经成功产生、准备交给布局页时才调用。
    /// 失败、超时和取消路径不会调用本方法，因此不会触碰最后一份有效 Preview。
    /// 新 Workspace Frozen Preview 与 legacy Preview 互斥，成功替换前先清掉另一条只读展示状态。
    /// </summary>
    internal void PrepareLogicalWorkspacePreviewReplacement()
    {
        if ((_logicalWorkspaceSession is not null || _workspaceFrozenPreview is not null) && _draft is null)
            _isLayoutEditing = false;

        ClearAiWorkspaceFrozenPreview();
    }

    /// <summary>
    /// 新 Workspace Preview 成功结果准备替换当前只读 Preview 时使用。
    /// 这里只释放 UI editing 标记，不创建 Candidate 或 Writer authority。
    /// </summary>
    internal void PrepareWorkspaceFrozenPreviewReplacement()
    {
        if ((_workspaceFrozenPreview is not null || _logicalWorkspaceSession is not null) && _draft is null)
            _isLayoutEditing = false;
    }
}
