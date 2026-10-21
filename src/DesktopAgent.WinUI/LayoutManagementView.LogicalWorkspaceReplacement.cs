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
        // 保留已经验证过的 legacy replacement 合同，同时让新 Workspace Preview 遵守同一生命周期语义。
        if (_logicalWorkspaceSession is not null && _draft is null)
            _isLayoutEditing = false;
        if (_workspaceFrozenPreview is not null && _draft is null)
            _isLayoutEditing = false;

        DisableWorkspaceFrozenPreviewApplyEvidence();
        ClearAiWorkspaceFrozenPreview();
        RefreshApplyButtonFromCurrentEvidence();
    }

    /// <summary>
    /// 新 Workspace Preview 成功结果准备替换当前只读 Preview 时使用。
    /// 这里只释放 UI editing 标记并销毁上一份 Preview 的一次性 Apply 入口；
    /// 新 authority 只能在新的 Frozen Preview 成功加载后重新开放。
    /// </summary>
    internal void PrepareWorkspaceFrozenPreviewReplacement()
    {
        if (_workspaceFrozenPreview is not null && _draft is null)
            _isLayoutEditing = false;
        if (_logicalWorkspaceSession is not null && _draft is null)
            _isLayoutEditing = false;

        DisableWorkspaceFrozenPreviewApplyEvidence();
    }
}
