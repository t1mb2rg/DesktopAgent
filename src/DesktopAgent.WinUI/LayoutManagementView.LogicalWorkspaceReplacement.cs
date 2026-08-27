namespace DesktopAgent.WinUI;

public sealed partial class LayoutManagementView
{
    /// <summary>
    /// 只有新的 PlanningSession 已经成功产生、准备交给布局页时才调用。
    /// 旧 Frozen Preview 是只读结果，不应像未保存 Draft 一样阻止新的成功结果替换它；
    /// 失败、超时和取消路径不会调用本方法，因此不会触碰最后一份有效 Preview。
    /// </summary>
    internal void PrepareLogicalWorkspacePreviewReplacement()
    {
        if (_logicalWorkspaceSession is not null && _draft is null)
            _isLayoutEditing = false;
    }
}
